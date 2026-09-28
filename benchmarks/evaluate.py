from __future__ import annotations

import argparse
import csv
import json
import re
from dataclasses import asdict, dataclass
from pathlib import Path
from statistics import mean


DIACRITICS = re.compile(r"[\u064B-\u065F\u0670]")
SPACES = re.compile(r"\s+")
PUNCTUATION = re.compile(r"[،؛؟.!,:;\"'«»()\[\]{}]")


def normalize(text: str) -> str:
    text = DIACRITICS.sub("", text.strip().lower())
    text = text.translate(str.maketrans({"أ": "ا", "إ": "ا", "آ": "ا", "ى": "ي"}))
    text = PUNCTUATION.sub(" ", text)
    return SPACES.sub(" ", text).strip()


def edit_distance(reference: list[str], hypothesis: list[str]) -> int:
    previous = list(range(len(hypothesis) + 1))
    for index, ref_item in enumerate(reference, 1):
        current = [index]
        for hyp_index, hyp_item in enumerate(hypothesis, 1):
            substitution = previous[hyp_index - 1] + (ref_item != hyp_item)
            current.append(min(previous[hyp_index] + 1, current[-1] + 1, substitution))
        previous = current
    return previous[-1]


def error_rate(reference: str, hypothesis: str, characters: bool = False) -> float:
    ref = list(normalize(reference).replace(" ", "")) if characters else normalize(reference).split()
    hyp = list(normalize(hypothesis).replace(" ", "")) if characters else normalize(hypothesis).split()
    return edit_distance(ref, hyp) / max(1, len(ref))


@dataclass(frozen=True)
class Row:
    id: str
    reference: str
    hypothesis: str
    wer: float
    cer: float
    audio_seconds: float
    inference_seconds: float
    real_time_factor: float


def main() -> int:
    parser = argparse.ArgumentParser(description="Score a transcription result JSONL file.")
    parser.add_argument("results", type=Path, help="JSONL with id/reference/hypothesis and timings")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    rows: list[Row] = []
    for line in args.results.read_text(encoding="utf-8").splitlines():
        item = json.loads(line)
        audio_seconds = float(item.get("audio_seconds", 0))
        inference_seconds = float(item.get("inference_seconds", 0))
        rows.append(Row(
            id=str(item["id"]),
            reference=str(item["reference"]),
            hypothesis=str(item["hypothesis"]),
            wer=error_rate(str(item["reference"]), str(item["hypothesis"])),
            cer=error_rate(str(item["reference"]), str(item["hypothesis"]), characters=True),
            audio_seconds=audio_seconds,
            inference_seconds=inference_seconds,
            real_time_factor=inference_seconds / audio_seconds if audio_seconds else 0,
        ))

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=Row.__dataclass_fields__.keys())
        writer.writeheader()
        writer.writerows(asdict(row) for row in rows)

    summary = {
        "count": len(rows),
        "mean_wer": mean(row.wer for row in rows) if rows else None,
        "mean_cer": mean(row.cer for row in rows) if rows else None,
        "mean_rtf": mean(row.real_time_factor for row in rows) if rows else None,
    }
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

