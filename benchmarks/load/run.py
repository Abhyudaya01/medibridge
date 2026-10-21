"""Run a bounded k6 workload and persist actual metrics, including failed runs."""
import argparse
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]


def extract_metrics(raw):
    metrics = raw['metrics']
    latency = metrics['claim_submission_duration']['values']
    count = metrics['claim_submissions']['values']
    failure = metrics['claim_submission_failed']['values']
    result = {'requests': count['count'], 'requestsPerSecond': count['rate'],
              'p50Ms': latency['p(50)'], 'p95Ms': latency['p(95)'], 'p99Ms': latency['p(99)'],
              'failureRate': failure['rate'], 'failedRequests': failure['passes']}
    if not all(isinstance(v, (int, float)) and math.isfinite(v) and v >= 0 for v in result.values()):
        raise ValueError('Missing, negative, or non-finite k6 metrics.')
    if result['requests'] < 1 or not result['p50Ms'] <= result['p95Ms'] <= result['p99Ms'] or result['failureRate'] > 1:
        raise ValueError('Inconsistent k6 metrics.')
    return result


def append_result(path, report, artifact):
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        path.write_text('# Measured benchmark results\n\nSynthetic data only. Chaos uses a virtual clock; load measures HTTP submission latency, not end-to-end recovery.\n\n| Completed UTC | Harness | Run ID | Status | Target | Measured result | Artifact |\n|---|---|---|---|---|---|---|\n')
    metrics = report['metrics']
    detail = (f"{metrics['requests']} requests; {metrics['requestsPerSecond']:.4f} req/s; p50/p95/p99 "
              f"{metrics['p50Ms']:.3f}/{metrics['p95Ms']:.3f}/{metrics['p99Ms']:.3f} ms; "
              f"failed {metrics['failedRequests']} ({100 * metrics['failureRate']:.3f}%)") if metrics else 'No complete k6 metrics; see error in artifact'
    target = f"{report['baseUrl']} / {report['sku']} / v{report['appVersion']}".replace('|', '/')
    link = os.path.relpath(artifact, path.parent)
    with path.open('a') as file:
        file.write(f"| {report['completedAtUtc']} | load | {report['runId']} | {report['status']} | {target} | {detail} | `{link}` |\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url', default='http://127.0.0.1:5080')
    parser.add_argument('--smoke', action='store_true')
    parser.add_argument('--live-approved', action='store_true', help='Only after explicit approval of the live test')
    parser.add_argument('--sku', default='local')
    parser.add_argument('--output', type=Path, default=ROOT / 'benchmarks/results/load.json')
    parser.add_argument('--results', type=Path, default=ROOT / 'benchmarks/RESULTS.md')
    args = parser.parse_args()
    url = args.base_url.rstrip('/')
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme not in ('http', 'https') or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ('', '/'):
        parser.error('Use an HTTP(S) origin without credentials, path, query, or fragment.')
    if parsed.hostname not in ('127.0.0.1', 'localhost', '::1') and not args.live_approved:
        parser.error('A non-local run requires explicit live approval and --live-approved.')
    with urllib.request.urlopen(url + '/health', timeout=60) as response:
        health = json.load(response)
    if health.get('status') != 'ok':
        raise RuntimeError('Target is not healthy.')
    run_id = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S') + '-' + uuid.uuid4().hex[:6]
    started = datetime.now(timezone.utc).isoformat()
    source = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    k6_version = subprocess.check_output(['k6', 'version'], text=True).strip()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='medibridge-k6-') as directory:
        summary = Path(directory) / 'summary.json'
        config = Path(directory) / 'config.json'
        config.write_text('{}')
        env = dict(os.environ, BASE_URL=url, RUN_ID=run_id, SUMMARY_PATH=str(summary), SMOKE=str(args.smoke).lower())
        # Use explicit config and metric options; do not load user-specific k6 defaults.
        command = ['k6', 'run', '--quiet', '--no-usage-report', '--config', str(config), str(Path(__file__).with_name('validate.js'))]
        timer = time.monotonic()
        raw, metrics, error = None, None, None
        exit_code = None
        try:
            child = subprocess.run(command, env=env, timeout=210 if not args.smoke else 60)
            exit_code = child.returncode
            raw = json.loads(summary.read_text())
            metrics = extract_metrics(raw)
            if exit_code:
                error = f'k6 exited with {exit_code}; thresholds or execution failed.'
        except (subprocess.TimeoutExpired, OSError, ValueError, KeyError) as exception:
            error = str(exception)
        report = {'schemaVersion': '1', 'runId': run_id, 'startedAtUtc': started,
                  'completedAtUtc': datetime.now(timezone.utc).isoformat(), 'sourceCommit': source,
                  'baseUrl': url, 'appVersion': health['version'], 'sku': args.sku, 'k6Version': k6_version,
                  'profile': '3-iteration-local-smoke' if args.smoke else '150s-ramp-0-5-10-10-0',
                  'maxVus': 1 if args.smoke else 10, 'thinkTimeSeconds': 1, 'wallSeconds': time.monotonic() - timer,
                  'measurement': 'POST /api/claims duration (all responses, milliseconds); submission throughput, not completed-claim throughput',
                  'status': 'completed' if not error else 'failed', 'exitCode': exit_code, 'error': error,
                  'metrics': metrics, 'rawSummary': raw}
        args.output.write_text(json.dumps(report, indent=2, allow_nan=False) + '\n')
        append_result(args.results, report, args.output)
        print(json.dumps({k: v for k, v in report.items() if k != 'rawSummary'}, indent=2))
        return 0 if not error else 1


if __name__ == '__main__':
    raise SystemExit(main())
