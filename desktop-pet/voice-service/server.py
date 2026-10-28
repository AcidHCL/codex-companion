"""Token-authenticated loopback TTS server, launched and owned by DesktopPet."""
from __future__ import annotations

import argparse
import base64
import hmac
import json
import os
import re
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

# Set before imports of torch/HF. The running server never downloads models.
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
os.environ["DO_NOT_TRACK"] = "1"
os.environ["TOKENIZERS_PARALLELISM"] = "false"

from engine import Config, VOICE, VoiceEngine, VoiceError

REQUEST_ID = re.compile(r"^[A-Za-z0-9_-]{1,100}$")


def install_fast_retirement(engine, exit_process=None):
    """Retire only this authenticated, app-owned worker; never another model."""
    retired = threading.Event()
    engine.retire_callback = retired.set
    if exit_process is None:
        exit_process = os._exit
    def watch():
        retired.wait()
        # Let the loopback /unload acknowledgement or a sanitized error flush.
        # The app additionally waits for its exact worker PID when unloading.
        time.sleep(.25)
        exit_process(0)
    threading.Thread(target=watch, name="voice-retirement", daemon=True).start()
    return retired


def make_handler(engine, token, port):
    class Handler(BaseHTTPRequestHandler):
        server_version = "AIPetVoice/1"
        sys_version = ""

        def log_message(self, *_):
            pass

        def setup(self):
            super().setup()
            self.connection.settimeout(30)

        def json_response(self, status, body):
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.end_headers()
            self.wfile.write(data)

        def authorized(self):
            host = self.headers.get("Host", "")
            if self.client_address[0] != "127.0.0.1" or host not in (f"127.0.0.1:{port}", f"localhost:{port}") or self.headers.get("Origin", ""):
                self.json_response(403, {"error": "forbidden"})
                return False
            supplied = self.headers.get("X-AIPet-Voice-Token", "")
            if not hmac.compare_digest(supplied.encode("utf-8"), token.encode("utf-8")):
                self.json_response(401, {"error": "unauthorized"})
                return False
            return True

        def do_GET(self):
            if not self.authorized():
                return
            if self.path == "/health":
                self.json_response(200, engine.health())
            else:
                self.json_response(404, {"error": "not_found"})

        def do_POST(self):
            if not self.authorized():
                return
            try:
                if self.headers.get("Transfer-Encoding") or not self.headers.get("Content-Type", "").lower().startswith("application/json"):
                    raise VoiceError("invalid_request", "必须发送 JSON 请求。", 400)
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= 16384:
                    raise VoiceError("invalid_length", "请求过大或为空。", 413)
                raw = self.rfile.read(length)
                if len(raw) != length:
                    raise VoiceError("invalid_request", "请求不完整。", 400)
                body = json.loads(raw)
                if not isinstance(body, dict):
                    raise ValueError()
                if self.path == "/unload":
                    if body:
                        raise ValueError()
                    engine.request_unload()
                    self.json_response(202, {"status": "unloading"})
                    return
                if self.path == "/retention":
                    if set(body) != {"active"} or not isinstance(body["active"], bool):
                        raise ValueError()
                    engine.retain(body["active"])
                    self.json_response(200, {"status": "retained" if body["active"] else "released"})
                    return
                request_id = body.get("request_id")
                if not isinstance(request_id, str) or not REQUEST_ID.fullmatch(request_id):
                    raise ValueError()
                if self.path == "/preload":
                    if set(body) != {"request_id"}:
                        raise ValueError()
                    self.json_response(200, engine.preload(request_id))
                elif self.path == "/cancel":
                    if set(body) != {"request_id"}:
                        raise ValueError()
                    self.json_response(200, {"status": "cancelled", "active": engine.cancel(request_id)})
                elif self.path in ("/v1/audio/speech", "/v1/audio/stream"):
                    if set(body) != {"input", "voice", "request_id"}:
                        raise ValueError()
                    text = body.get("input")
                    if body.get("voice") != VOICE or not isinstance(text, str) or not 1 <= len(text.strip()) <= 2000 or "\x00" in text:
                        raise ValueError()
                    if self.path == "/v1/audio/stream":
                        if not hasattr(engine, "stream"):
                            raise VoiceError("streaming_unavailable", "当前语音后端不支持流式播放。", 409)
                        iterator = engine.stream(text.strip(), request_id)
                        try:
                            first = next(iterator)
                            self.send_response(200)
                            self.send_header("Content-Type", "application/x-ndjson; charset=utf-8")
                            self.send_header("Cache-Control", "no-store")
                            self.send_header("X-Content-Type-Options", "nosniff")
                            self.send_header("Connection", "close")
                            self.end_headers()
                            self.close_connection = True
                            def send(item):
                                if item["type"] == "audio":
                                    item = {"type": "audio", "data": base64.b64encode(item["pcm"]).decode("ascii")}
                                self.wfile.write(json.dumps(item, ensure_ascii=False).encode("utf-8") + b"\n")
                                self.wfile.flush()
                            try:
                                send(first)
                                for item in iterator:
                                    send(item)
                            except VoiceError as exc:
                                send({"type": "error", "code": exc.code, "message": exc.message})
                        finally:
                            iterator.close()
                        return
                    wav = engine.synthesize(text.strip(), request_id)
                    self.send_response(200)
                    self.send_header("Content-Type", "audio/wav")
                    self.send_header("Content-Length", str(len(wav)))
                    self.send_header("Cache-Control", "no-store")
                    self.send_header("X-Content-Type-Options", "nosniff")
                    self.end_headers()
                    self.wfile.write(wav)
                else:
                    self.json_response(404, {"error": "not_found"})
            except VoiceError as exc:
                self.json_response(exc.status, {"error": exc.code, "message": exc.message})
            except (ValueError, TypeError, UnicodeError):
                self.json_response(400, {"error": "invalid_request"})
            except (ConnectionError, TimeoutError, OSError):
                # A disconnected client must never cause request-text traceback logging.
                if "request_id" in locals() and isinstance(request_id, str):
                    engine.cancel(request_id)

    return Handler


def watch_parent(pid, server):
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
    # Parent owns this process. Exit promptly even during CUDA loading/generation.
    os._exit(0)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=18766)
    parser.add_argument("--parent-pid", type=int, required=True)
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535 or args.parent_pid <= 0:
        raise SystemExit("Invalid launch arguments")
    token = sys.stdin.readline(1024).strip()
    if not 32 <= len(token) <= 256 or not token.isascii():
        raise SystemExit("Missing session token")
    config = Config.load(Path(__file__).resolve().parent)
    if config.backend == "faster":
        from faster_engine import FasterVoiceEngine
        engine = FasterVoiceEngine(config)
        install_fast_retirement(engine)
    else:
        engine = VoiceEngine(config)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(engine, token, args.port))
    server.daemon_threads = True
    # Avoid default traceback printing from request-handler failures.
    server.handle_error = lambda *_: None
    threading.Thread(target=watch_parent, args=(args.parent_pid, server), daemon=True).start()
    try:
        server.serve_forever(poll_interval=.25)
    finally:
        engine.stop.set()
        server.server_close()


if __name__ == "__main__":
    main()
