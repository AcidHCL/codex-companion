"""One-shot offline ASR. Audio enters only through stdin; no recording or networking."""
from __future__ import annotations

import argparse
import array
import contextlib
import io
import json
import logging
import math
import os
from pathlib import Path
import sys
import threading
import wave

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
os.environ["DO_NOT_TRACK"] = "1"
os.environ["TOKENIZERS_PARALLELISM"] = "false"
os.environ["CUDA_VISIBLE_DEVICES"] = ""

MODEL = "faster-whisper-small"
MAX_FRAMES = 16000 * 60
MAX_BYTES = MAX_FRAMES * 2 + 4096


class AsrError(Exception):
    def __init__(self, code: str, message: str):
        self.code, self.message = code, message
        super().__init__(code)


def model_directory(service: Path) -> Path:
    config_path = service / "config.json"
    data = json.loads(config_path.read_text(encoding="utf-8-sig")) if config_path.is_file() else {}
    configured = data.get("model_path", "../../.tools/deepseek-asr-models/" + MODEL)
    if not isinstance(configured, str):
        raise AsrError("invalid_config", "语音识别配置无效。")
    candidate = Path(configured)
    resolved = candidate if candidate.is_absolute() else (service / candidate).resolve()
    conventional = configured.replace("\\", "/") == "../../.tools/deepseek-asr-models/" + MODEL
    if conventional and not (resolved / "model.bin").is_file():
        for ancestor in service.parents:
            found = ancestor / ".tools" / "deepseek-asr-models" / MODEL
            if (found / "model.bin").is_file():
                return found
    return resolved


def read_wave(raw: bytes) -> tuple[bytes, bool]:
    if len(raw) < 44 or len(raw) > MAX_BYTES or raw[:4] != b"RIFF" or raw[8:12] != b"WAVE":
        raise AsrError("invalid_audio", "录音不是有效的 WAV 文件。")
    try:
        with wave.open(io.BytesIO(raw), "rb") as audio:
            if (audio.getnchannels(), audio.getsampwidth(), audio.getframerate(), audio.getcomptype()) != (1, 2, 16000, "NONE"):
                raise AsrError("invalid_audio", "录音需要 16kHz 单声道 PCM16 格式。")
            count = audio.getnframes()
            if count > MAX_FRAMES:
                raise AsrError("audio_too_long", "每段录音最长 60 秒。")
            if count <= 0:
                raise AsrError("invalid_audio", "录音没有包含音频数据。")
            pcm = audio.readframes(count)
            if len(pcm) != count * 2:
                raise AsrError("invalid_audio", "录音数据不完整。")
    except (wave.Error, EOFError):
        raise AsrError("invalid_audio", "录音不是有效的 WAV 文件。") from None
    values = array.array("h", pcm)
    if sys.byteorder != "little":
        values.byteswap()
    rms = math.sqrt(sum(value * value for value in values) / len(values)) / 32768
    peak = max(abs(value) for value in values) / 32768
    # Avoid loading a model for near-zero input; VAD handles ordinary non-speech.
    silent = count < 4000 or (rms < .0005 and peak < .003)
    return pcm, silent


def recognition_prompt(ui_language: str) -> str:
    return ("Natural conversational English and Mandarin Chinese. Transcribe the spoken language as heard."
            if ui_language == "en-US" else "以下是简体中文和英语的自然对话。")


def recognize(raw: bytes, service: Path, ui_language: str = "zh-CN") -> dict:
    pcm, silent = read_wave(raw)
    if silent:
        return {"text": "", "language": "", "language_probability": 0.0}
    directory = model_directory(service)
    if not all((directory / name).is_file() for name in ("model.bin", "config.json", "tokenizer.json", "vocabulary.txt")):
        raise AsrError("model_missing", "本地语音识别模型尚未安装，请运行 asr-service 中的安装脚本。")
    import numpy as np
    from faster_whisper import WhisperModel
    model = WhisperModel(str(directory), device="cpu", compute_type="int8",
                         cpu_threads=min(8, os.cpu_count() or 4), num_workers=1,
                         local_files_only=True)
    samples = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768
    segments, info = model.transcribe(
        samples, task="transcribe", language=None, beam_size=5,
        temperature=0.0, condition_on_previous_text=False,
        vad_filter=True, vad_parameters={"min_silence_duration_ms": 300, "speech_pad_ms": 200},
        initial_prompt=recognition_prompt(ui_language),
    )
    text = "".join(segment.text for segment in segments).strip()
    return {"text": text, "language": info.language if text else "",
            "language_probability": float(info.language_probability) if text else 0.0}


def watch_parent(pid: int):
    if os.name == "nt":
        import ctypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel.OpenProcess(0x00100000, False, pid)
        if not handle:
            os._exit(0)
        try:
            while kernel.WaitForSingleObject(handle, 1000) == 258:
                pass
        finally:
            kernel.CloseHandle(handle)
    else:
        import time
        while True:
            try:
                os.kill(pid, 0)
            except OSError:
                break
            time.sleep(1)
    os._exit(0)


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="strict")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser()
    parser.add_argument("--parent-pid", required=True, type=int)
    parser.add_argument("--ui-language", choices=("zh-CN", "en-US"), default="zh-CN")
    args = parser.parse_args()
    if args.parent_pid <= 0:
        parser.error("Invalid parent process")
    threading.Thread(target=watch_parent, args=(args.parent_pid,), daemon=True).start()
    logging.disable(logging.CRITICAL)
    try:
        raw = sys.stdin.buffer.read(MAX_BYTES + 1)
        # Dependency warnings/debug prints must never corrupt the JSON channel.
        # They are discarded, rather than logging possible transcript fragments.
        with open(os.devnull, "w", encoding="utf-8") as sink:
            with contextlib.redirect_stdout(sink), contextlib.redirect_stderr(sink):
                result = recognize(raw, Path(__file__).resolve().parent, args.ui_language)
        print(json.dumps(result, ensure_ascii=False), flush=True)
        return 0
    except AsrError as exc:
        print(json.dumps({"error": exc.code, "message": exc.message}, ensure_ascii=False), file=sys.stderr, flush=True)
        return 2
    except ModuleNotFoundError:
        print(json.dumps({"error": "dependency_missing", "message": "本地语音识别依赖不完整，请重新运行安装脚本。"}, ensure_ascii=False), file=sys.stderr, flush=True)
        return 3
    except Exception:
        # Do not expose decoder exceptions that can contain user speech/text.
        print(json.dumps({"error": "transcription_failed", "message": "本地语音识别失败，请重试。"}, ensure_ascii=False), file=sys.stderr, flush=True)
        return 4


if __name__ == "__main__":
    raise SystemExit(main())
