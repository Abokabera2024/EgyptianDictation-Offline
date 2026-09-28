import unittest
from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "benchmarks"))
from evaluate import error_rate, normalize  # noqa: E402


class EvaluationTests(unittest.TestCase):
    def test_normalizes_arabic_variants_and_punctuation(self) -> None:
        self.assertEqual("اختبار الي", normalize("إخْتِبار، إلى!"))

    def test_word_error_rate(self) -> None:
        self.assertAlmostEqual(1 / 3, error_rate("هذا نص صحيح", "هذا نص مختلف"))

    def test_character_error_rate(self) -> None:
        self.assertEqual(0, error_rate("إلى", "الي", characters=True))


if __name__ == "__main__":
    unittest.main()

