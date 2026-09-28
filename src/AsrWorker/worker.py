from __future__ import annotations

import argparse
import json
import logging
import sys
import time
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Protocol


LOG = logging.getLogger("egyptian_dictation.worker")

# Vocabulary hints, not replacement rules: keep valid words such as الدعوة
# and التوقعات when those are what the speaker actually says.
ARABIC_PROMPT = (
    "إملاء بالعربية واللهجة المصرية. "
    "الاطلاع على ملف الدعوى، بيان ما إذا كان، فحص المستندات، "
    "كشف التزييف والتزوير، مضاهاة الخطوط، التوقيع، التوقيعات، "
    "الخصائص الخطية، توصيف المقرر، الاعتماد."
)


def configure_utf8_stdio() -> None:
    """Keep the NDJSON protocol Unicode-safe on Windows, including embedded Python."""
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8")
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8")


class Backend(Protocol):
    def load(self, model: str, device: str, compute_type: str) -> None: ...
    def transcribe(self, audio_path: Path, language: str) -> "Transcription": ...


@dataclass(frozen=True)
class Transcription:
    text: str
    audio_seconds: float
    inference_seconds: float

    @property
    def real_time_factor(self) -> float:
        if self.audio_seconds <= 0:
            return 0.0
        return self.inference_seconds / self.audio_seconds


class FasterWhisperBackend:
    def __init__(self) -> None:
        self._model: Any | None = None

    def load(self, model: str, device: str, compute_type: str) -> None:
        from faster_whisper import WhisperModel

        self._model = WhisperModel(model, device=device, compute_type=compute_type)

    def transcribe(self, audio_path: Path, language: str) -> Transcription:
        if self._model is None:
            raise RuntimeError("model_not_loaded")

        started = time.perf_counter()
        segments, info = self._model.transcribe(
            str(audio_path),
            language=language,
            beam_size=5,
            best_of=1,
            temperature=0.0,
            initial_prompt=ARABIC_PROMPT if language == "ar" else None,
            vad_filter=True,
            vad_parameters={"speech_pad_ms": 400},
            condition_on_previous_text=False,
        )
        text = " ".join(segment.text.strip() for segment in segments if segment.text.strip()).strip()
        inference_seconds = time.perf_counter() - started
        return Transcription(text, float(info.duration), inference_seconds)


class FakeBackend:
    def __init__(self, text: str = "اختبار ناجح") -> None:
        self._text = text
        self.loaded = False

    def load(self, model: str, device: str, compute_type: str) -> None:
        self.loaded = True

    def transcribe(self, audio_path: Path, language: str) -> Transcription:
        if not self.loaded:
            raise RuntimeError("model_not_loaded")
        if not audio_path.exists():
            raise FileNotFoundError(audio_path)
        return Transcription(self._text, 1.0, 0.01)


def handle(request: dict[str, Any], backend: Backend) -> dict[str, Any]:
    request_id = str(request.get("id", ""))
    command = request.get("command")

    if command == "load":
        backend.load(
            str(request["model"]),
            str(request.get("device", "cpu")),
            str(request.get("compute_type", "int8")),
        )
        return {"id": request_id, "ok": True, "state": "ready"}

    if command == "transcribe":
        path = Path(str(request["audio_path"])).resolve()
        result = backend.transcribe(path, str(request.get("language", "ar")))
        payload = asdict(result)
        payload["real_time_factor"] = result.real_time_factor
        return {"id": request_id, "ok": True, **payload}

    if command == "ping":
        return {"id": request_id, "ok": True, "state": "alive"}

    if command == "shutdown":
        return {"id": request_id, "ok": True, "state": "stopping", "shutdown": True}

    raise ValueError(f"unknown_command:{command}")


def run(backend: Backend) -> int:
    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue
        request_id = ""
        try:
            request = json.loads(line)
            request_id = str(request.get("id", ""))
            response = handle(request, backend)
        except Exception as exc:  # process boundary: errors must become protocol messages
            LOG.exception("Request failed")
            response = {"id": request_id, "ok": False, "error": type(exc).__name__, "message": str(exc)}

        print(json.dumps(response, ensure_ascii=False), flush=True)
        if response.get("shutdown"):
            return 0
    return 0


def main() -> int:
    configure_utf8_stdio()
    parser = argparse.ArgumentParser()
    parser.add_argument("--fake", action="store_true", help="Use the deterministic test backend")
    parser.add_argument("--fake-text", default="اختبار ناجح")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, stream=sys.stderr)
    backend: Backend = FakeBackend(args.fake_text) if args.fake else FasterWhisperBackend()
    return run(backend)


if __name__ == "__main__":
    raise SystemExit(main())
