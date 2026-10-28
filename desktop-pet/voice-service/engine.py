"""Local, offline Qwen3-TTS runtime. No user text or session secrets are logged."""
from __future__ import annotations

import gc
import io
import json
import re
import threading
import time
from dataclasses import dataclass
from pathlib import Path

VOICE = "deepseek-let-me-go"
MODEL = "Qwen3-TTS-12Hz-1.7B-Base"


class VoiceError(Exception):
    def __init__(self, code: str, message: str, status: int = 503):
        super().__init__(message)
        self.code, self.message, self.status = code, message, status


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

    @classmethod
    def load(cls, service_dir: Path):
        workspace = service_dir.parent.parent
        root = workspace / ".tools" / "deepseek-voice-models"
        cfg = cls(root / MODEL, service_dir / "reference.wav")
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
                candidate = ancestor / ".tools" / "deepseek-voice-models" / MODEL
                if candidate.joinpath("model.safetensors").is_file():
                    cfg.model_path = candidate
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
        self.last_used = time.monotonic()
        self.status, self.message = "idle", "本地语音模型将在首次朗读时加载。"
        self.stop = threading.Event()
        self.unload_requested = threading.Event()
        self.retention_until = 0.0
        if not self.config.model_path.joinpath("model.safetensors").is_file() or not self.config.reference_path.is_file():
            self.status, self.message = "error", "语音模型或参考音频尚未准备完成。"
        threading.Thread(target=self._idle_loop, name="voice-idle", daemon=True).start()

    def health(self):
        with self.guard:
            return {"status": self.status, "model": MODEL, "voice": VOICE,
                    "message": self.message, "queued": len(self.requests),
                    "backend": self.config.backend, "loaded": self.model is not None and self.prompt is not None,
                    "retained": time.monotonic() < self.retention_until,
                    "streaming": self.config.backend == "faster",
                    "retiring": bool(getattr(self, "retiring", False)),
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
            if self.model is not None and self.prompt is None:
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
        if self.model is not None and self.prompt is not None:
            return
        if self.model is not None:
            self._unload()
        self._set("loading", "正在加载本地参考音色模型。")
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
        import soundfile as sf
        audio, rate = sf.read(str(self.config.reference_path), dtype="float32")
        # The reference is a local separated singing stem. Without a verified
        # transcript use speaker-embedding mode; never invent the reference text.
        self.prompt = self.model.create_voice_clone_prompt(ref_audio=(audio, rate), x_vector_only_mode=True)
        self._check_cancel(cancelled)
        self._set("ready", "参考音色已加载；歌曲与口语的听感可能仍有差异。")

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
                    wavs, sample_rate = self.model.generate_voice_clone(
                        text=chunk, language="Chinese", voice_clone_prompt=self.prompt,
                        non_streaming_mode=True,
                        max_new_tokens=min(1600, max(200, len(chunk) * 14)),
                    )
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
            if exc.code == "cancelled" and self.prompt is None and self.model is not None:
                self._unload()
            elif exc.code == "cancelled" and self.prompt is not None:
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
