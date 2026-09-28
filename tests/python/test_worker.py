import json
import tempfile
import unittest
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import Mock


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src" / "AsrWorker"))

from worker import FakeBackend, FasterWhisperBackend, handle  # noqa: E402


class WorkerProtocolTests(unittest.TestCase):
    def test_accuracy_settings_preserve_recognized_words(self) -> None:
        backend = FasterWhisperBackend()
        backend._model = Mock()
        backend._model.transcribe.return_value = (
            iter([SimpleNamespace(text="الدعوة والتوقعات")]),
            SimpleNamespace(duration=2.0),
        )
        result = backend.transcribe(Path("sample.wav"), "ar")
        self.assertEqual("الدعوة والتوقعات", result.text)
        options = backend._model.transcribe.call_args.kwargs
        self.assertEqual(5, options["beam_size"])
        self.assertEqual(0.0, options["temperature"])
        self.assertIn("التوقيعات", options["initial_prompt"])

    def test_non_arabic_has_no_arabic_prompt(self) -> None:
        backend = FasterWhisperBackend()
        backend._model = Mock()
        backend._model.transcribe.return_value = (iter([]), SimpleNamespace(duration=1.0))
        self.assertEqual("", backend.transcribe(Path("sample.wav"), "en").text)
        self.assertIsNone(backend._model.transcribe.call_args.kwargs["initial_prompt"])

    def test_load_ping_and_transcribe(self) -> None:
        backend = FakeBackend("نص تجريبي")
        loaded = handle({"id": "1", "command": "load", "model": "fake"}, backend)
        self.assertTrue(loaded["ok"])

        with tempfile.NamedTemporaryFile(suffix=".wav") as audio:
            result = handle(
                {"id": "2", "command": "transcribe", "audio_path": audio.name, "language": "ar"},
                backend,
            )

        self.assertEqual("نص تجريبي", result["text"])
        self.assertAlmostEqual(0.01, result["real_time_factor"])

    def test_rejects_transcription_before_load(self) -> None:
        with tempfile.NamedTemporaryFile(suffix=".wav") as audio:
            with self.assertRaisesRegex(RuntimeError, "model_not_loaded"):
                handle({"id": "1", "command": "transcribe", "audio_path": audio.name}, FakeBackend())

    def test_protocol_response_is_unicode_json(self) -> None:
        encoded = json.dumps({"text": "صوت عربي"}, ensure_ascii=False)
        self.assertIn("صوت عربي", encoded)


if __name__ == "__main__":
    unittest.main()
