import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('load_runner', Path(__file__).with_name('run.py'))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class LoadSummaryTests(unittest.TestCase):
    def raw(self):
        return {'metrics': {
            'claim_submission_duration': {'values': {'p(50)': 20, 'p(95)': 50, 'p(99)': 80}},
            'claim_submissions': {'values': {'count': 100, 'rate': 4}},
            'claim_submission_failed': {'values': {'rate': 0.02, 'passes': 2}},
        }}

    def test_extracts_post_metrics_not_setup_or_global_http_totals(self):
        raw = self.raw()
        raw['metrics']['http_reqs'] = {'values': {'count': 5000, 'rate': 500}}
        result = runner.extract_metrics(raw)
        self.assertEqual(result['requests'], 100)
        self.assertEqual(result['requestsPerSecond'], 4)
        self.assertEqual(result['p99Ms'], 80)
        self.assertEqual(result['failedRequests'], 2)

    def test_rejects_missing_or_invalid_metrics(self):
        with self.assertRaises(KeyError):
            runner.extract_metrics({'metrics': {}})
        raw = self.raw()
        raw['metrics']['claim_submission_duration']['values']['p(95)'] = float('nan')
        with self.assertRaises(ValueError):
            runner.extract_metrics(raw)

    def test_appends_rows_without_overwriting_previous_runs(self):
        with tempfile.TemporaryDirectory() as folder:
            ledger = Path(folder) / 'RESULTS.md'
            report = {'metrics': runner.extract_metrics(self.raw()), 'baseUrl': 'http://localhost:5080',
                      'sku': 'local', 'appVersion': 'test', 'completedAtUtc': 'test', 'runId': 'SYN-TEST', 'status': 'failed'}
            for _ in range(2):
                runner.append_result(ledger, report, Path(folder) / 'results/load.json')
            text = ledger.read_text()
            self.assertEqual(text.count('| load | SYN-TEST | failed |'), 2)
            self.assertIn('20.000/50.000/80.000 ms', text)
            self.assertIn('failed 2 (2.000%)', text)


if __name__ == '__main__':
    unittest.main()
