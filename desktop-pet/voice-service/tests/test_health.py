"""Read-only telemetry checks: no torch, weights, CUDA or audio dependencies."""
import sys
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from engine import MODEL, VOICE, VoiceEngine


def engine():
    # Do not start the idle worker or inspect a real model installation.
    value = VoiceEngine.__new__(VoiceEngine)
    value.guard = threading.RLock()
    value.requests = {}
    value.model = value.prompt = value.torch = None
    value.status, value.message = "idle", "fixture"
    value.retention_until = 0.0
    value.unload_requested = threading.Event()
    value.config = SimpleNamespace(backend="faster", model_name=MODEL, voice_id=VOICE, requires_reference=True)
    return value


class CudaProbe:
    def __init__(self, initialized, allocated=0, reserved=0):
        self.initialized = initialized
        self.allocated, self.reserved = allocated, reserved
        self.reads = []

    def is_initialized(self):
        self.reads.append("initialized")
        return self.initialized

    def memory_allocated(self, device):
        self.reads.append(("allocated", device))
        return self.allocated

    def memory_reserved(self, device):
        self.reads.append(("reserved", device))
        return self.reserved

    def __getattr__(self, name):
        raise AssertionError("Telemetry unexpectedly called CUDA " + name)


class VoiceHealthTests(unittest.TestCase):
    def test_unstarted_runtime_does_not_import_torch_or_report_zero(self):
        value = engine()
        before = set(sys.modules)
        health = value.health()
        self.assertEqual(set(sys.modules), before)
        self.assertEqual(health["model"], MODEL)
        self.assertFalse(health["loaded"])
        self.assertIsNone(health["device"])
        self.assertIsNone(health["allocated_bytes"])
        self.assertIsNone(health["reserved_bytes"])

    def test_uninitialized_cuda_is_not_initialized_by_poll(self):
        value = engine()
        cuda = CudaProbe(False)
        value.torch = SimpleNamespace(cuda=cuda)
        self.assertIsNone(value.health()["allocated_bytes"])
        self.assertEqual(cuda.reads, ["initialized"])

    def test_loaded_runtime_reports_allocator_usage(self):
        value = engine()
        value.status = "ready"
        value.model = value.prompt = object()
        cuda = CudaProbe(True, 3 * 1024**3, 5 * 1024**3)
        value.torch = SimpleNamespace(cuda=cuda)
        health = value.health()
        self.assertTrue(health["loaded"])
        self.assertEqual(health["device"], "cuda:0")
        self.assertEqual(health["allocated_bytes"], 3 * 1024**3)
        self.assertEqual(health["reserved_bytes"], 5 * 1024**3)

    def test_zero_is_measured_and_remains_distinct_from_unknown(self):
        value = engine()
        value.torch = SimpleNamespace(cuda=CudaProbe(True))
        health = value.health()
        self.assertEqual(health["allocated_bytes"], 0)
        self.assertEqual(health["reserved_bytes"], 0)

    def test_pending_unload_is_visible_before_retirement(self):
        value = engine()
        value.status = "ready"
        value.unload_requested.set()
        self.assertEqual(value.health()["status"], "unloading")

    def test_retirement_does_not_pretend_weights_already_released(self):
        value = engine()
        value.model = value.prompt = object()
        value.retiring = True
        health = value.health()
        self.assertTrue(health["retiring"])
        self.assertTrue(health["loaded"])
        self.assertEqual(health["status"], "unloading")

    def test_metric_failure_does_not_break_health_or_claim_zero(self):
        value = engine()
        class BrokenCuda(CudaProbe):
            def memory_allocated(self, device):
                raise RuntimeError("fixture")
        value.torch = SimpleNamespace(cuda=BrokenCuda(True))
        self.assertIsNone(value.health()["allocated_bytes"])


if __name__ == "__main__":
    unittest.main()
