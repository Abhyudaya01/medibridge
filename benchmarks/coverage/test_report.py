import json
from pathlib import Path
import tempfile
import unittest
import report


class CoverageReportTests(unittest.TestCase):
    def xml(self, package='MediBridge.Core', hits='1', rate='0.5', covered='1'):
        return f'''<coverage lines-valid="2" lines-covered="{covered}" line-rate="{rate}"><packages><package name="{package}"><classes><class filename="src/MediBridge.Core/Example.cs"><lines><line number="1" hits="{hits}"/><line number="2" hits="0"/></lines></class></classes></package></packages></coverage>'''

    def test_counts_and_percentage_are_derived_from_exported_lines(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'coverage.xml'
            path.write_text(self.xml(hits='7'))
            result = report.summarize(path)
            self.assertEqual((result['coveredLines'], result['totalLines'], result['lineCoveragePercent']), (1, 2, 50))
            self.assertEqual(result['files'][0]['coveredLines'], 1)

    def test_wrong_assembly_inconsistent_percentage_and_fake_line_totals_are_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'coverage.xml'
            for xml in (self.xml(package='Other'), self.xml(rate='0.9'), self.xml(hits='0'), self.xml(covered='3')):
                with self.subTest(xml=xml):
                    path.write_text(xml)
                    with self.assertRaises(ValueError):
                        report.summarize(path)

    def test_success_and_failure_runs_write_json_and_ledger(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'coverage.xml'
            output = Path(folder) / 'results' / 'coverage.json'
            path.write_text(self.xml())
            self.assertEqual(report.run(path, output), 0)
            self.assertEqual(json.loads(output.read_text())['status'], 'completed')
            path.write_text('<broken>')
            self.assertEqual(report.run(path, output), 1)
            self.assertEqual(json.loads(output.read_text())['status'], 'failed')
            self.assertIn('| failed |', (Path(folder) / 'RESULTS.md').read_text())


if __name__ == '__main__':
    unittest.main()
