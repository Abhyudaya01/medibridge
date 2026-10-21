import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('azure_runner', Path(__file__).with_name('azure_run.py'))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class AzureWindowTests(unittest.TestCase):
    def test_subscription_guard_prevents_mutation(self):
        with patch.object(runner.subprocess, 'check_output', return_value='wrong-subscription\n'), patch.object(runner.subprocess, 'run') as mutate:
            with self.assertRaisesRegex(RuntimeError, 'guard failed'):
                runner.az(['appservice', 'plan', 'update'], change=True)
            mutate.assert_not_called()

    def test_failed_k6_restores_and_verifies_f1(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'infra').mkdir()
            (root / 'infra/azure.env').write_text('AZURE_RESOURCE_GROUP=SYN-GROUP\nAZURE_APP_SERVICE_PLAN=SYN-PLAN\nAZURE_REGION=westus\nAZURE_APP_URL=https://synthetic.invalid\n')
            changes = []
            sku = 'F1'

            def fake_az(arguments, change=False):
                nonlocal sku
                if change:
                    sku = arguments[arguments.index('--sku') + 1]
                    changes.append(sku)
                return {'sku': sku, 'location': 'West US', 'capacity': 1}

            class Healthy:
                def __enter__(self):
                    import io
                    return io.BytesIO(b'{"status":"ok"}')
                def __exit__(self, *args):
                    pass

            with patch('builtins.print'), patch.object(runner, 'ROOT', root), patch.object(runner, 'az', side_effect=fake_az), \
                 patch.object(runner.sys, 'argv', ['azure_run.py', '--approved']), \
                 patch.object(runner.urllib.request, 'urlopen', return_value=Healthy()), \
                 patch.object(runner.subprocess, 'run', return_value=type('Failed', (), {'returncode': 99})()):
                self.assertEqual(runner.main(), 99)
            self.assertEqual(changes, ['B1', 'F1'])
            import json
            record = json.loads((root / 'benchmarks/results/load-azure-operation.json').read_text())
            self.assertEqual(record['final']['sku'], 'F1')
            self.assertIsNone(record['rollbackError'])

    def test_explicit_approval_flag_is_required(self):
        with patch('sys.stderr'), patch.object(runner.sys, 'argv', ['azure_run.py']), patch.object(runner, 'az') as azure:
            with self.assertRaises(SystemExit):
                runner.main()
            azure.assert_not_called()


if __name__ == '__main__':
    unittest.main()
