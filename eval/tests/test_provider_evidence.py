"""Observer failures must not silently certify model-run evidence."""
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))
from harness import metrics


class ProviderEvidenceTests(unittest.TestCase):
    def parse(self, errors):
        with tempfile.TemporaryDirectory() as directory:
            stream = Path(directory, "subject.stream.jsonl")
            stream.write_text(json.dumps({"type": "turn.completed", "usage": {"input_tokens": 1, "output_tokens": 1}}))
            Path(directory, "subject.identity.json").write_text(json.dumps({
                "source": "response.model", "models": ["actual"], "observations": [], "errors": errors}))
            return metrics.parse(str(stream), 8091, host="codex")

    def test_unfinished_metadata_is_not_sufficient_evidence(self):
        row = self.parse(["metadata collection still active"])
        self.assertTrue(any("metadata collection" in p for p in row["evidenceProblems"]))

    def test_provider_http_failures_count_toward_retry_threshold(self):
        row = self.parse(["upstream HTTP 503"] * 3)
        self.assertEqual(row["apiRetries"], 3)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
