"""Idle policy and loopback protocol fixtures; no models, torch, CUDA or audio."""
import http.client
import json
import sys
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from engine import VoiceEngine
from faster_engine import FasterVoiceEngine
from server import make_handler


def fixture():
    value = VoiceEngine.__new__(VoiceEngine)
    value.guard = threading.RLock()
    value.generation_lock = threading.Lock()
    value.unload_requested = threading.Event()
    value.config = SimpleNamespace(idle_seconds=60)
    value.model = object()
    value.last_used = 100.0
    value.retention_until = 0.0
    value.idle_seconds_override = None
    return value


class OneTick:
    def __init__(self):
        self.count = 0

    def wait(self, _):
        self.count += 1
        return self.count > 1


class RetentionTests(unittest.TestCase):
    def test_finite_timeout_counts_from_last_model_use(self):
        engine = fixture()
        with patch("engine.time.monotonic", return_value=200):
            engine.retain(False, 300)
            self.assertEqual(engine.last_used, 100)
            self.assertFalse(engine._idle_due())
        with patch("engine.time.monotonic", return_value=399):
            engine.retain(False, 300)
            self.assertEqual(engine.last_used, 100)
            self.assertFalse(engine._idle_due())
        with patch("engine.time.monotonic", return_value=400):
            self.assertTrue(engine._idle_due())

    def test_never_policy_requires_live_owner_renewals(self):
        engine = fixture()
        with patch("engine.time.monotonic", return_value=1000):
            engine.retain(True)
            self.assertFalse(engine._idle_due())
        with patch("engine.time.monotonic", return_value=1030):
            engine.retain(True)
            self.assertFalse(engine._idle_due())
        with patch("engine.time.monotonic", return_value=1076):
            self.assertTrue(engine._idle_due())
        self.assertEqual(engine.last_used, 100)

    def test_manual_unload_overrides_active_lease(self):
        engine = fixture()
        with patch("engine.time.monotonic", return_value=120):
            engine.retain(True, 3600)
            engine.unload_requested.set()
            self.assertTrue(engine._idle_due())

    def test_return_to_legacy_policy_restores_configured_idle_duration(self):
        engine = fixture()
        with patch("engine.time.monotonic", return_value=180):
            engine.retain(False, 3600)
            self.assertFalse(engine._idle_due())
            engine.retain(False)
            self.assertIsNone(engine.idle_seconds_override)
            self.assertTrue(engine._idle_due())

    def test_policy_change_to_shorter_duration_applies_to_existing_idle_model(self):
        engine = fixture()
        with patch("engine.time.monotonic", return_value=400):
            engine.retain(False, 600)
            self.assertFalse(engine._idle_due())
            engine.retain(False, 60)
            self.assertTrue(engine._idle_due())

    def test_idle_tick_does_not_interrupt_model_generation(self):
        engine = fixture()
        engine.stop = OneTick()
        calls = []
        engine._unload = lambda: calls.append("unload")
        engine.generation_lock.acquire()
        try:
            with patch("engine.time.monotonic", return_value=1000):
                engine._idle_loop()
        finally:
            engine.generation_lock.release()
        self.assertEqual(calls, [])

    def test_idle_tick_rechecks_policy_after_acquiring_generation_lock(self):
        engine = fixture()
        engine.stop = OneTick()
        calls = []
        engine._unload = lambda: calls.append("unload")

        class ChangedLock:
            def acquire(self, **_):
                engine.retain(True)
                return True

            def release(self):
                pass

        engine.generation_lock = ChangedLock()
        with patch("engine.time.monotonic", return_value=1000):
            engine._idle_loop()
        self.assertEqual(calls, [])

    def test_idle_timeout_uses_faster_worker_retirement_for_full_cuda_release(self):
        engine = fixture()
        engine.__class__ = FasterVoiceEngine
        engine.stop = OneTick()
        retired = []
        engine.retire_callback = lambda: retired.append(True)
        engine.status = "ready"
        engine.message = "fixture"
        before = set(sys.modules)
        with patch("engine.time.monotonic", return_value=1000):
            engine._idle_loop()
        self.assertEqual(retired, [True])
        self.assertTrue(engine.retiring)
        self.assertEqual(set(sys.modules), before)


class RetentionHttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.calls = []
        cls.engine = SimpleNamespace(retain=lambda active, idle_seconds=None: cls.calls.append((active, idle_seconds)))
        cls.token = "synthetic-retention-token"
        cls.http = ThreadingHTTPServer(("127.0.0.1", 0), BaseHTTPRequestHandler)
        cls.http.RequestHandlerClass = make_handler(cls.engine, cls.token, cls.http.server_port)
        cls.thread = threading.Thread(target=cls.http.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.http.shutdown()
        cls.http.server_close()
        cls.thread.join(2)

    def send(self, body):
        connection = http.client.HTTPConnection("127.0.0.1", self.http.server_port, timeout=2)
        try:
            connection.request("POST", "/retention", json.dumps(body), {
                "Content-Type": "application/json", "X-AIPet-Voice-Token": self.token})
            response = connection.getresponse()
            response.read()
            return response.status
        finally:
            connection.close()

    def test_accepts_legacy_lease_and_finite_idle_policy_without_inference(self):
        self.assertEqual(self.send({"active": True}), 200)
        self.assertEqual(self.calls[-1], (True, None))
        self.assertEqual(self.send({"active": False, "idle_seconds": 300}), 200)
        self.assertEqual(self.calls[-1], (False, 300))
        self.assertEqual(self.send({"active": True, "idle_seconds": None}), 200)
        self.assertEqual(self.calls[-1], (True, None))

    def test_rejects_invalid_policies_without_mutating_worker(self):
        previous = len(self.calls)
        for seconds in (-1, 0, 86401, True, 1.5, "300"):
            with self.subTest(seconds=seconds):
                self.assertEqual(self.send({"active": False, "idle_seconds": seconds}), 400)
        self.assertEqual(self.send({"active": "yes", "idle_seconds": 300}), 400)
        self.assertEqual(self.send({"active": True, "idle_seconds": 300, "unknown": True}), 400)
        self.assertEqual(len(self.calls), previous)


if __name__ == "__main__":
    unittest.main()
