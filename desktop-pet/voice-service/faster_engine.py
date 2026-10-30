"""Offline CUDA-graph speech generation for reference, designed and preset voices."""
from __future__ import annotations

import io
import threading
import time
import wave

from engine import VoiceEngine, VoiceError, split_text


class FasterVoiceEngine(VoiceEngine):
    # The app-owned production worker retires on unload. CUDA graph pools can
    # retain references after ordinary Python GC; process exit is the reliable
    # release boundary. Direct diagnostic instances do not receive this hook.
    retire_callback = None
    retiring = False

    def _unload(self):
        if self.retire_callback is not None:
            self.retiring = True
            self.unload_requested.clear()
            self._set("unloading", "正在退出语音工作进程并释放显存。")
            self.retire_callback()
            return
        super()._unload()

    def _load(self, cancelled):
        if self.retiring:
            raise VoiceError("retiring", "语音引擎正在卸载，请稍后重新加载。", 409)
        if self.unload_requested.is_set():
            self._unload()
            if self.retiring:
                raise VoiceError("retiring", "语音引擎正在卸载，请稍后重新加载。", 409)
        if self.is_loaded:
            return
        if self.model is not None:
            self._unload()
            if self.retiring:
                raise VoiceError("retiring", "语音引擎正在卸载，请稍后重新加载。", 409)
        self._check_assets()
        self._set("loading", "正在加载所选音色并预热语音加速引擎。")
        import torch
        self.torch = torch
        if not torch.cuda.is_available():
            raise VoiceError("cuda_unavailable", "未检测到可用的 NVIDIA CUDA 显卡。")
        available, _ = torch.cuda.mem_get_info()
        if available < self.config.minimum_free_gib * 1024 ** 3:
            raise VoiceError("gpu_busy", "可用显存不足，暂缓加载语音模型；没有改变聊天模型。")
        self._check_cancel(cancelled)
        from faster_qwen3_tts import FasterQwen3TTS
        self.model = FasterQwen3TTS.from_pretrained(
            str(self.config.model_path), device="cuda:0", dtype=torch.bfloat16,
            attn_implementation="sdpa", backend="torch", max_seq_len=2048,
            local_files_only=True,
        )
        self._check_cancel(cancelled)
        if self.config.requires_reference:
            import soundfile as sf
            audio, rate = sf.read(str(self.config.reference_path), dtype="float32")
            # Base keeps its singing reference; fixed voices use their own
            # audio/transcript pair in ICL mode.
            self.prompt = self.model.model.create_voice_clone_prompt(
                ref_audio=(audio, rate), **self._clone_prompt_options(),
            )
        self._check_cancel(cancelled)
        self.model.warmup()
        self._check_cancel(cancelled)
        self.prepared = True
        self._set("ready", "所选音色已预热，支持连续流式播放。")

    def _generate_stream(self, text):
        options = {"text": text, "language": self.config.language, "non_streaming_mode": True,
                   "chunk_size": self.config.stream_chunk_size,
                   "max_new_tokens": min(1600, max(200, len(text) * 14))}
        if self.config.model_id == "voice-design":
            return self.model.generate_voice_design_streaming(**options, instruct=self.config.instruct)
        if self.config.model_id == "custom-voice":
            return self.model.generate_custom_voice_streaming(**options, speaker=self.config.speaker, instruct=self.config.instruct)
        return self.model.generate_voice_clone_streaming(**options, voice_clone_prompt=self.prompt,
                                                         xvec_only=self.config.x_vector_only)

    def stream(self, text, request_id):
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
                acquired = self.generation_lock.acquire(timeout=.1)
            self._check_cancel(event)
            self._load(event)
            self._check_cancel(event)
            self._set("synthesizing", "正在连续生成语音。")
            yield {"type": "format", "sample_rate": 24000, "channels": 1, "sample_format": "s16le"}
            import numpy as np
            total_bytes = 0
            for part in split_text(text):
                self._check_cancel(event)
                generator = self._generate_stream(part)
                try:
                    for samples, sample_rate, _ in generator:
                        self._check_cancel(event)
                        if time.monotonic() > deadline:
                            raise VoiceError("timeout", "语音生成超时。", 504)
                        samples = np.asarray(samples, dtype=np.float32).reshape(-1)
                        if sample_rate != 24000 or not np.all(np.isfinite(samples)):
                            raise VoiceError("invalid_audio", "语音引擎返回了无效音频。")
                        if not len(samples):
                            continue
                        pcm = (np.clip(samples, -.96, .96) * 32767).astype("<i2").tobytes()
                        total_bytes += len(pcm)
                        if total_bytes > 64 * 1024 ** 2:
                            raise VoiceError("audio_too_large", "语音内容过长，请分段朗读。")
                        yield {"type": "audio", "pcm": pcm}
                finally:
                    generator.close()
                # Preserve the existing short phrase boundary pause, rather
                # than opening a new player or waiting for a whole WAV.
                yield {"type": "audio", "pcm": bytes(int(24000 * .12) * 2)}
            if total_bytes == 0:
                raise VoiceError("invalid_audio", "语音模型未返回声音。")
            self._set("ready", "本地音色已就绪。")
            yield {"type": "done"}
        except VoiceError as exc:
            if exc.code == "cancelled" and self.model is not None and not self.is_loaded:
                self._unload()
            if exc.code not in ("cancelled", "duplicate_request", "queue_full"):
                self._set("error", exc.message)
            raise
        except Exception as exc:
            if self.torch is not None and isinstance(exc, self.torch.cuda.OutOfMemoryError):
                self._unload()
                self._set("error", "显存不足，已释放语音模型。")
                raise VoiceError("gpu_out_of_memory", "显存不足，已释放语音模型；聊天模型保持原设置。") from None
            self._unload()
            self._set("error", "本地流式语音失败，请检查运行环境。")
            raise VoiceError("synthesis_failed", "本地流式语音失败，请检查运行环境。") from None
        finally:
            self.last_used = time.monotonic()
            if acquired:
                if self.status == "synthesizing" and self.model is not None:
                    self._set("ready", "朗读已结束，本地音色仍可使用。")
                self.generation_lock.release()
            with self.guard:
                self.requests.pop(request_id, None)

    def synthesize(self, text, request_id):
        output = io.BytesIO()
        with wave.open(output, "wb") as writer:
            writer.setnchannels(1)
            writer.setsampwidth(2)
            writer.setframerate(24000)
            for item in self.stream(text, request_id):
                if item["type"] == "audio":
                    writer.writeframesraw(item["pcm"])
        return output.getvalue()
