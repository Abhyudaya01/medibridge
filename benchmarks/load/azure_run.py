"""Explicitly approved B1 load-test window, with subscription guard and F1 restoration."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess
import sys
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
SUBSCRIPTION = '36637271-a6f8-4110-9be1-55f112612c8c'


def az(arguments, change=False):
    if change:
        active = subprocess.check_output(['az', 'account', 'show', '--query', 'id', '-o', 'tsv'], text=True).strip()
        if active != SUBSCRIPTION:
            raise RuntimeError('Azure subscription guard failed; no mutation attempted.')
    result = subprocess.run(['az', *arguments, '--subscription', SUBSCRIPTION, '--only-show-errors', '-o', 'json'],
                            capture_output=True, text=True, timeout=300)
    if result.returncode:
        raise RuntimeError('Azure plan operation failed: ' + result.stderr.strip())
    return json.loads(result.stdout) if result.stdout.strip() else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--approved', action='store_true', help='Requires prior explicit human approval for billable B1 and live k6')
    args = parser.parse_args()
    if not args.approved:
        parser.error('Ask for explicit approval before using --approved. This temporarily enables billable B1.')
    settings = dict(line.split('=', 1) for line in (ROOT / 'infra/azure.env').read_text().splitlines() if line and not line.startswith('#'))
    group, plan, region = settings['AZURE_RESOURCE_GROUP'], settings['AZURE_APP_SERVICE_PLAN'], settings['AZURE_REGION']
    identity = ['-g', group, '-n', plan]

    def current():
        return az(['appservice', 'plan', 'show', *identity, '--query', '{sku:sku.name,location:location,capacity:sku.capacity}'])

    before = current()
    if before['sku'] != 'F1' or before['location'].replace(' ', '').lower() != region.lower():
        raise RuntimeError('Expected F1 in the recorded region before beginning; refusing to change another plan state.')
    operation = {'startedAtUtc': datetime.now(timezone.utc).isoformat(), 'subscription': SUBSCRIPTION,
                 'plan': plan, 'region': region, 'initial': before, 'scaleUp': None, 'final': None, 'error': None, 'rollbackError': None, 'loadExitCode': None}
    exit_code = 1
    try:
        az(['appservice', 'plan', 'update', *identity, '--sku', 'B1', '--number-of-workers', '1'], change=True)
        operation['scaleUp'] = current()
        if operation['scaleUp']['sku'] != 'B1':
            raise RuntimeError('B1 was not confirmed; load test was not started.')
        print('B1 confirmed; waiting for health before the bounded live test.', flush=True)
        deadline = time.monotonic() + 180
        while True:
            try:
                with urllib.request.urlopen(settings['AZURE_APP_URL'] + '/health', timeout=20) as response:
                    if json.load(response).get('status') == 'ok':
                        break
            except (OSError, ValueError):
                pass
            if time.monotonic() > deadline:
                raise RuntimeError('App did not become healthy in the B1 warmup window.')
            time.sleep(5)
        exit_code = subprocess.run([sys.executable, str(Path(__file__).with_name('run.py')), '--base-url', settings['AZURE_APP_URL'],
                                    '--sku', 'B1', '--live-approved'], cwd=ROOT, timeout=330).returncode
        operation['loadExitCode'] = exit_code
    except (RuntimeError, OSError, subprocess.SubprocessError) as exception:
        operation['error'] = str(exception)
    finally:
        # Always attempt rollback, including a scale-up command that timed out after Azure accepted it.
        try:
            for attempt in range(3):
                try:
                    az(['appservice', 'plan', 'update', *identity, '--sku', 'F1', '--number-of-workers', '1'], change=True)
                    operation['final'] = current()
                    if operation['final']['sku'] != 'F1':
                        raise RuntimeError('F1 restoration was not confirmed.')
                    print('Final SKU verified: F1.', flush=True)
                    break
                except (RuntimeError, OSError, subprocess.SubprocessError):
                    if attempt == 2:
                        raise
                    time.sleep(10)
        except (RuntimeError, OSError, subprocess.SubprocessError) as exception:
            operation['rollbackError'] = str(exception)
            exit_code = 1
        operation['completedAtUtc'] = datetime.now(timezone.utc).isoformat()
        folder = ROOT / 'benchmarks/results'
        folder.mkdir(parents=True, exist_ok=True)
        (folder / 'load-azure-operation.json').write_text(json.dumps(operation, indent=2) + '\n')
        print(json.dumps(operation, indent=2))
    return exit_code


if __name__ == '__main__':
    raise SystemExit(main())
