"""Local, offline Qwen3-TTS runtime. No user text or session secrets are logged."""
from __future__ import annotations

import gc
import io
import json
import os
import re
import stat
import threading
import time
import unicodedata
import wave
from dataclasses import dataclass, replace
from pathlib import Path

VOICE = "deepseek-let-me-go"
MODEL = "Qwen3-TTS-12Hz-1.7B-Base"
MODELS = {
    "base": MODEL,
    "voice-design": "Qwen3-TTS-12Hz-1.7B-VoiceDesign",
    "custom-voice": "Qwen3-TTS-12Hz-1.7B-CustomVoice",
}
# Bundled fixed voices share Base weights. Only these names can select a local
# reference; the launch protocol never accepts reference paths or URLs.
FIXED_VOICES = {"gpt-fixed": ("gpt-dragon-v1", "gpt-dragon-fixed-v1")}
SAVED_REFERENCE_ID = re.compile(r"[0-9a-f]{32}\Z")
SPEAKERS = frozenset(("Vivian", "Serena", "Uncle_Fu", "Dylan", "Eric", "Ryan", "Aiden", "Ono_Anna", "Sohee"))
LANGUAGES = frozenset(("Auto", "Chinese", "English", "Japanese", "Korean", "German", "French", "Russian", "Portuguese", "Spanish", "Italian"))


class VoiceError(Exception):
    def __init__(self, code: str, message: str, status: int = 503):
        super().__init__(message)
        self.code, self.message, self.status = code, message, status


def local_saved_voice_root():
    """Match .NET LocalApplicationData; never accept this root from a request."""
    if os.name != "nt":
        return None  # Non-Windows fixtures explicitly inject a trusted root.
    import ctypes
    folder = ctypes.create_unicode_buffer(32768)
    get_folder = ctypes.WinDLL("shell32").SHGetFolderPathW
    get_folder.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p,
                           ctypes.c_uint, ctypes.c_wchar_p]
    get_folder.restype = ctypes.c_long
    if get_folder(None, 0x001C, None, 0, folder) != 0 or not folder.value:
        raise ValueError("Local voice library unavailable")
    return Path(folder.value) / "AIPet" / "voices"


@dataclass
class Config:
    model_path: Path
    reference_path: Path
    idle_seconds: float = 60
    minimum_free_gib: float = 7
    maximum_queue: int = 4
    request_timeout: float = 480
    backend: str = "legacy"
    stream_chunk_size: int = 8
    model_id: str = "base"
    speaker: str = "Vivian"
    language: str = "Chinese"
    instruct: str = ""
    service_dir: Path | None = None
    reference_text_path: Path | None = None
    saved_voice_root: Path | None = None
    reference_id: str | None = None

    @property
    def is_fixed_voice(self):
        return self.model_id in FIXED_VOICES or self.model_id == "saved-voice"

    @property
    def model_name(self):
        return MODEL if self.is_fixed_voice else MODELS[self.model_id]

    @property
    def requires_reference(self):
        return self.model_id == "base" or self.is_fixed_voice

    @property
    def x_vector_only(self):
        return not self.is_fixed_voice

    @property
    def voice_id(self):
        if self.model_id == "saved-voice":
            return "saved-voice:" + self.reference_id
        if self.model_id in FIXED_VOICES:
            return FIXED_VOICES[self.model_id][1]
        return VOICE if self.requires_reference else (
            "custom-voice:" + self.speaker if self.model_id == "custom-voice" else "voice-design")

    def with_profile(self, profile=None):
        """Apply an immutable launch choice, never a caller-supplied path or URL."""
        if profile is None:
            return self
        if not isinstance(profile, dict) or set(profile) - {"model_id", "speaker", "language", "instruct", "reference_id"}:
            raise ValueError("Invalid voice profile")
        model_id = profile.get("model_id", "base")
        speaker = profile.get("speaker", "Vivian")
        language = profile.get("language", "Chinese")
        instruct = profile.get("instruct", "")
        reference_id = profile.get("reference_id")
        if not isinstance(model_id, str) or model_id not in MODELS and model_id not in FIXED_VOICES and model_id != "saved-voice":
            raise ValueError("Invalid voice model")
        if model_id == "saved-voice":
            if not isinstance(reference_id, str) or not SAVED_REFERENCE_ID.fullmatch(reference_id):
                raise ValueError("Invalid saved reference identifier")
        elif reference_id not in (None, ""):
            raise ValueError("Reference identifier needs a saved voice")
        if not isinstance(speaker, str) or speaker not in SPEAKERS:
            raise ValueError("Invalid voice speaker")
        if not isinstance(language, str) or language not in LANGUAGES:
            raise ValueError("Invalid voice language")
        if not isinstance(instruct, str) or len(instruct) > 600 or any(
                unicodedata.category(c) in ("Cc", "Cs") and c not in ("\n", "\t") for c in instruct):
            raise ValueError("Invalid voice instruction")
        instruct = instruct.strip()
        if model_id == "voice-design" and not instruct:
            raise ValueError("Voice design needs a description")
        # Base keeps the configured path and reference verbatim. New modes live
        # beside that resolved model, including in nested published app folders.
        model_path = self.model_path if model_id in ("base", "saved-voice") or model_id in FIXED_VOICES else self.model_path.parent / MODELS[model_id]
        reference_path, reference_text_path = self.reference_path, None
        if model_id in FIXED_VOICES:
            if self.service_dir is None:
                raise ValueError("Fixed voice needs a trusted service directory")
            root = self.service_dir.resolve()
            reference_dir = root / "references" / FIXED_VOICES[model_id][0]
            reference_path = reference_dir / "reference.wav"
            reference_text_path = reference_dir / "reference.txt"
            if any(not path.resolve().is_relative_to(root) for path in (reference_path, reference_text_path)):
                raise ValueError("Fixed reference must stay inside the voice service")
        if model_id == "saved-voice":
            if self.saved_voice_root is None or not self.saved_voice_root.is_absolute():
                raise ValueError("Saved voice needs a trusted library directory")
            reference_dir = self.saved_voice_root / reference_id
            reference_path = reference_dir / "reference.wav"
            reference_text_path = reference_dir / "reference.txt"
            instruct = ""
        cfg = replace(self, model_path=model_path, reference_path=reference_path,
                       reference_text_path=reference_text_path, model_id=model_id, speaker=speaker,
                       language=language, instruct=instruct, reference_id=reference_id if model_id == "saved-voice" else None)
        cfg.validate_saved_reference_paths()
        return cfg

    def validate_saved_reference_paths(self):
        if self.model_id != "saved-voice":
            return
        root = self.saved_voice_root
        if root is None or not root.is_absolute() or not isinstance(self.reference_id, str) or not SAVED_REFERENCE_ID.fullmatch(self.reference_id):
            raise ValueError("Invalid local voice library")
        # Check every component before resolve(), including the library itself.
        # Symlinks/junctions cannot make a valid ID read another local file.
        for target in (self.reference_path, self.reference_text_path):
            if target is None or not target.is_relative_to(root):
                raise ValueError("Saved reference escaped its library")
            for path in (target, *target.parents):
                try:
                    info = path.lstat()
                except FileNotFoundError:
                    continue
                except OSError:
                    raise ValueError("Cannot validate saved reference") from None
                if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                    raise ValueError("Saved reference contains a reparse point")
            if not target.resolve().is_relative_to(root.resolve()):
                raise ValueError("Saved reference escaped its library")

    def validate_saved_reference_audio(self):
        if self.model_id != "saved-voice":
            return
        try:
            self.validate_saved_reference_paths()
            if not self.reference_path.is_file():
                raise VoiceError("reference_missing", "已保存音色的参考音频不存在。")
            if not 44 <= self.reference_path.stat().st_size <= 10 * 1024 * 1024:
                raise ValueError("Invalid saved audio size")
            with wave.open(str(self.reference_path), "rb") as audio:
                count = audio.getnframes()
                if audio.getnchannels() != 1 or audio.getsampwidth() != 2 or audio.getframerate() != 24000 or not 24000 <= count <= 720000:
                    raise ValueError("Invalid saved audio format")
                if len(audio.readframes(count)) != count * 2:
                    raise ValueError("Truncated saved audio")
        except (OSError, ValueError, wave.Error, EOFError):
            raise VoiceError("reference_invalid", "已保存音色的参考音频无效，请重新生成并保存。") from None

    def read_reference_text(self):
        if not self.is_fixed_voice:
            return None
        try:
            if self.model_id == "saved-voice":
                try:
                    self.validate_saved_reference_paths()
                except ValueError:
                    raise VoiceError("reference_text_invalid", "已保存音色的参考台词路径无效。") from None
            if self.reference_text_path is None or not self.reference_text_path.is_file():
                raise VoiceError("reference_text_missing", "固定音色的参考台词尚未准备完成。")
            if self.reference_text_path.stat().st_size > 8192:
                raise VoiceError("reference_text_invalid", "固定音色的参考台词无效。")
            text = self.reference_text_path.read_text(encoding="utf-8-sig").strip()
            if not text or len(text) > (100 if self.model_id == "saved-voice" else 1200) or any(
                    unicodedata.category(c) in ("Cc", "Cs") and c not in ("\n", "\t") for c in text):
                raise VoiceError("reference_text_invalid", "固定音色的参考台词无效。")
            return text
        except (OSError, UnicodeError):
            raise VoiceError("reference_text_invalid", "无法读取固定音色的参考台词。") from None

    @classmethod
    def load(cls, service_dir: Path):
        service_dir = service_dir.resolve()
        workspace = service_dir.parent.parent
        root = workspace / ".tools" / "deepseek-voice-models"
        cfg = cls(root / MODEL, service_dir / "reference.wav", service_dir=service_dir,
                  saved_voice_root=local_saved_voice_root())
        config_path = service_dir / "config.json"
        data = {}
        if config_path.is_file():
            data = json.loads(config_path.read_text(encoding="utf-8-sig"))
            for field in ("model_path", "reference_path"):
                if field in data:
                    candidate = Path(data[field])
                    setattr(cfg, field, candidate if candidate.is_absolute() else (service_dir / candidate).resolve())
            for field in ("idle_seconds", "minimum_free_gib", "request_timeout"):
                if field in data:
                    setattr(cfg, field, float(data[field]))
            if "backend" in data:
                cfg.backend = str(data["backend"])
            if cfg.backend not in ("legacy", "faster"):
                raise ValueError("Unknown local voice backend")
            if "stream_chunk_size" in data:
                cfg.stream_chunk_size = int(data["stream_chunk_size"])
            if not 4 <= cfg.stream_chunk_size <= 24:
                raise ValueError("Invalid stream chunk size")
        # Published app output can be several levels below the workspace. Only
        # the conventional relative model location gets ancestor discovery;
        # an explicit absolute/custom model path remains authoritative.
        configured = data.get("model_path", "")
        conventional = not configured or configured.replace("\\", "/") == "../../.tools/deepseek-voice-models/" + MODEL
        if conventional and not cfg.model_path.joinpath("model.safetensors").is_file():
            for ancestor in service_dir.parents:
                model_root = ancestor / ".tools" / "deepseek-voice-models"
                if any((model_root / name / "model.safetensors").is_file() for name in MODELS.values()):
                    # The Base file may be absent on a computer that installed
                    # only a preset/designed voice. Preserve the shared root.
                    cfg.model_path = model_root / MODEL
                    break
        return cfg


def split_text(text: str, limit: int = 100):
    # Splitting at punctuation limits activation use and bounds cancellation latency.
    pieces = re.findall(r"[^。！？!?；;\n]+[。！？!?；;\n]*|[。！？!?；;\n]+", text)
    chunks, current = [], ""
    for piece in pieces:
        if current and len(current) + len(piece) > limit:
            chunks.append(current.strip())
            current = ""
        while len(piece) > limit:
            if current:
                chunks.append(current.strip())
                current = ""
            boundary = max(piece.rfind(mark, 0, limit + 1) for mark in ("，", ",", " ", "、"))
            boundary = boundary + 1 if boundary > limit // 2 else limit
            chunks.append(piece[:boundary].strip())
            piece = piece[boundary:]
        current += piece
    if current.strip():
        chunks.append(current.strip())
    return [chunk for chunk in chunks if chunk]


class VoiceEngine:
    def __init__(self, config: Config):
        self.config = config
        self.guard = threading.RLock()
        self.generation_lock = threading.Lock()
        self.requests: dict[str, threading.Event] = {}
        self.cancelled_ids: dict[str, float] = {}
        self.model = self.prompt = self.torch = None
        self.prepared = False
        self.last_used = time.monotonic()
        self.status, self.message = "idle", "本地语音模型将在首次朗读时加载。"
        self.stop = threading.Event()
        self.unload_requested = threading.Event()
        self.retention_until = 0.0
        if not self.config.model_path.joinpath("model.safetensors").is_file():
            self.status, self.message = "error", "所选语音模型尚未下载完成。"
        elif self.config.requires_reference and not self.config.reference_path.is_file():
            self.status, self.message = "error", "参考音频尚未准备完成。"
        elif self.config.is_fixed_voice:
            try:
                self.config.validate_saved_reference_audio()
                self.config.read_reference_text()
            except VoiceError as exc:
                self.status, self.message = "error", exc.message
        threading.Thread(target=self._idle_loop, name="voice-idle", daemon=True).start()

    @property
    def is_loaded(self):
        return self.model is not None and (self.prompt is not None if self.config.requires_reference else self.prepared)

    def _check_assets(self):
        # Fail before importing torch or allocating any CUDA context.
        if not self.config.model_path.joinpath("model.safetensors").is_file():
            raise VoiceError("model_missing", "所选语音模型尚未下载完成，请先安装对应模型。")
        if self.config.requires_reference and not self.config.reference_path.is_file():
            raise VoiceError("reference_missing", "参考音频尚未准备完成。")
        self.config.validate_saved_reference_audio()
        self.config.read_reference_text()

    def _clone_prompt_options(self):
        if self.config.is_fixed_voice:
            return {"ref_text": self.config.read_reference_text(), "x_vector_only_mode": False}
        # DeepSeek keeps the original singing-reference embedding mode.
        return {"x_vector_only_mode": True}

    def health(self):
        with self.guard:
            # Inspect only an already initialized CUDA runtime. Polling this
            # endpoint must not import torch, create a context or load weights.
            device, allocated, reserved = None, None, None
            if self.torch is not None:
                try:
                    if self.torch.cuda.is_initialized():
                        device = "cuda:0"
                        allocated = int(self.torch.cuda.memory_allocated(0))
                        reserved = int(self.torch.cuda.memory_reserved(0))
                except (RuntimeError, AttributeError, TypeError, ValueError):
                    device, allocated, reserved = None, None, None
            retiring = bool(getattr(self, "retiring", False))
            status = "unloading" if retiring or self.unload_requested.is_set() else self.status
            return {"status": status, "model": self.config.model_name, "voice": self.config.voice_id,
                    "message": self.message, "queued": len(self.requests),
                    "backend": self.config.backend, "loaded": self.is_loaded,
                    "retained": time.monotonic() < self.retention_until,
                    "streaming": self.config.backend == "faster",
                    "retiring": retiring, "device": device,
                    "allocated_bytes": allocated, "reserved_bytes": reserved,
                    "cancellation": "generation-token-boundary; load/decode-results-discarded"}

    def _set(self, status, message):
        with self.guard:
            self.status, self.message = status, message

    def cancel(self, request_id):
        with self.guard:
            now = time.monotonic()
            self.cancelled_ids = {key: stamp for key, stamp in self.cancelled_ids.items() if now - stamp < 60}
            if len(self.cancelled_ids) >= 128:
                self.cancelled_ids.pop(next(iter(self.cancelled_ids)))
            self.cancelled_ids[request_id] = now
            event = self.requests.get(request_id)
            if event:
                event.set()
            return bool(event)

    def request_unload(self):
        self.unload_requested.set()
        with self.guard:
            for event in self.requests.values():
                event.set()

    def retain(self, active):
        # The client renews this lease while a chat is open. If it dies or stops
        # renewing, the ordinary idle policy still releases GPU memory.
        with self.guard:
            self.retention_until = time.monotonic() + 45 if active else 0.0

    def preload(self, request_id):
        with self.guard:
            if request_id in self.requests:
                raise VoiceError("duplicate_request", "重复的语音请求。", 409)
            if len(self.requests) >= self.config.maximum_queue:
                raise VoiceError("queue_full", "语音队列已满。", 429)
            event = threading.Event()
            if time.monotonic() - self.cancelled_ids.get(request_id, -1e9) < 60:
                event.set()
            self.requests[request_id] = event
        acquired = False
        deadline = time.monotonic() + self.config.request_timeout
        try:
            while not acquired:
                self._check_cancel(event)
                if time.monotonic() > deadline:
                    raise VoiceError("timeout", "语音预加载等待超时。", 504)
                acquired = self.generation_lock.acquire(timeout=.1)
            # An explicit unload wins over operations already waiting for it.
            self._check_cancel(event)
            self._load(event)
            self._check_cancel(event)
            return self.health()
        except VoiceError:
            if self.model is not None and not self.is_loaded:
                self._unload()
            raise
        except Exception:
            self._unload()
            raise VoiceError("preload_failed", "语音预加载失败，请检查可用显存和本地运行环境。") from None
        finally:
            self.last_used = time.monotonic()
            if acquired:
                self.generation_lock.release()
            with self.guard:
                self.requests.pop(request_id, None)

    def _unload(self):
        self.prompt = None
        self.model = None
        self.prepared = False
        gc.collect()
        if self.torch is not None and self.torch.cuda.is_available():
            self.torch.cuda.empty_cache()
        self._set("idle", "语音模型已释放显存，下一次朗读时自动加载。")
        self.unload_requested.clear()

    def _idle_loop(self):
        while not self.stop.wait(1):
            if self.unload_requested.is_set() or (self.model is not None and time.monotonic() >= self.retention_until and time.monotonic() - self.last_used >= self.config.idle_seconds):
                if self.generation_lock.acquire(blocking=False):
                    try:
                        self._unload()
                    finally:
                        self.generation_lock.release()

    def _load(self, cancelled):
        if self.unload_requested.is_set():
            self._unload()
        if self.is_loaded:
            return
        if self.model is not None:
            self._unload()
        self._check_assets()
        self._set("loading", "正在加载本地语音模型。")
        import torch
        self.torch = torch
        if not torch.cuda.is_available():
            raise VoiceError("cuda_unavailable", "未检测到可用的 NVIDIA CUDA 显卡。")
        available, _ = torch.cuda.mem_get_info()
        if available < self.config.minimum_free_gib * 1024 ** 3:
            raise VoiceError("gpu_busy", "显存不足，暂缓朗读；请等待本地聊天模型释放显存。")
        self._check_cancel(cancelled)
        from qwen_tts import Qwen3TTSModel
        self.model = Qwen3TTSModel.from_pretrained(
            str(self.config.model_path), device_map="cuda:0", dtype=torch.bfloat16,
            attn_implementation="sdpa", local_files_only=True,
        )
        self._check_cancel(cancelled)
        if self.config.requires_reference:
            import soundfile as sf
            audio, rate = sf.read(str(self.config.reference_path), dtype="float32")
            # Keep the existing singing-reference conditioning exactly as it was.
            self.prompt = self.model.create_voice_clone_prompt(ref_audio=(audio, rate), **self._clone_prompt_options())
        self._check_cancel(cancelled)
        self.prepared = True
        self._set("ready", "固定音色已加载，将复用同一参考声音。" if self.config.is_fixed_voice else
                  "参考音色已加载；歌曲与口语的听感可能仍有差异。" if self.config.requires_reference else "所选本地音色已加载。")

    def _generate(self, text):
        options = {"text": text, "language": self.config.language, "non_streaming_mode": True,
                   "max_new_tokens": min(1600, max(200, len(text) * 14))}
        if self.config.model_id == "voice-design":
            return self.model.generate_voice_design(**options, instruct=self.config.instruct)
        if self.config.model_id == "custom-voice":
            return self.model.generate_custom_voice(**options, speaker=self.config.speaker, instruct=self.config.instruct)
        return self.model.generate_voice_clone(**options, voice_clone_prompt=self.prompt)

    @staticmethod
    def _check_cancel(event):
        if event.is_set():
            raise VoiceError("cancelled", "朗读已取消。", 409)

    def synthesize(self, text, request_id):
        with self.guard:
            if request_id in self.requests:
                raise VoiceError("duplicate_request", "重复的语音请求。", 409)
            if len(self.requests) >= self.config.maximum_queue:
                raise VoiceError("queue_full", "语音队列已满。", 429)
            event = threading.Event()
            if time.monotonic() - self.cancelled_ids.get(request_id, -1e9) < 60:
                event.set()
            self.requests[request_id] = event
        acquired = False
        deadline = time.monotonic() + self.config.request_timeout
        try:
            while not acquired:
                self._check_cancel(event)
                if time.monotonic() > deadline:
                    raise VoiceError("timeout", "语音请求等待超时。", 504)
                acquired = self.generation_lock.acquire(timeout=0.1)
            self._load(event)
            self._check_cancel(event)
            import numpy as np
            import soundfile as sf
            from transformers import StoppingCriteria, StoppingCriteriaList

            class CancelCriteria(StoppingCriteria):
                def __call__(self, input_ids, scores, **kwargs):
                    return event.is_set() or time.monotonic() > deadline

            segments, sample_rate = [], 24000
            for chunk in split_text(text):
                self._check_cancel(event)
                # qwen-tts 0.1.1 drops unknown kwargs in its outer generate().
                # Inject cancellation at the underlying HF talker boundary for
                # this serialized call, and restore it even on failure.
                talker = self.model.model.talker
                original_generate = talker.generate
                def cancellable_generate(*args, **kwargs):
                    kwargs["stopping_criteria"] = StoppingCriteriaList([CancelCriteria()])
                    return original_generate(*args, **kwargs)
                talker.generate = cancellable_generate
                try:
                    wavs, sample_rate = self._generate(chunk)
                finally:
                    talker.generate = original_generate
                self._check_cancel(event)
                if time.monotonic() > deadline:
                    raise VoiceError("timeout", "语音生成超时。", 504)
                segments.append(np.asarray(wavs[0], dtype=np.float32))
                segments.append(np.zeros(int(sample_rate * .12), dtype=np.float32))
            audio = np.concatenate(segments)
            if not np.all(np.isfinite(audio)) or not len(audio):
                raise VoiceError("invalid_audio", "语音模型未返回有效音频。")
            peak = float(np.max(np.abs(audio)))
            if peak > .96:
                audio *= .96 / peak
            output = io.BytesIO()
            sf.write(output, audio, sample_rate, format="WAV", subtype="PCM_16")
            self._set("ready", "本地语音模型已就绪。")
            return output.getvalue()
        except VoiceError as exc:
            if exc.code == "cancelled" and self.model is not None and not self.is_loaded:
                self._unload()
            elif exc.code == "cancelled" and self.is_loaded:
                self._set("ready", "朗读已取消，本地语音模型仍可使用。")
            if exc.code not in ("cancelled", "duplicate_request", "queue_full"):
                self._set("error", exc.message)
            raise
        except Exception as exc:
            # No exception string is returned/logged: third-party errors may embed text.
            if self.torch is not None and isinstance(exc, self.torch.cuda.OutOfMemoryError):
                self._unload()
                self._set("error", "显存不足，已释放语音模型。")
                raise VoiceError("gpu_out_of_memory", "显存不足，已释放语音模型。") from None
            self._unload()
            self._set("error", "本地语音合成失败，请检查模型与运行环境。")
            raise VoiceError("synthesis_failed", "本地语音合成失败，请检查模型与运行环境。") from None
        finally:
            self.last_used = time.monotonic()
            if acquired:
                self.generation_lock.release()
            with self.guard:
                self.requests.pop(request_id, None)
