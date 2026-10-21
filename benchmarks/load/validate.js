import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

const base = (__ENV.BASE_URL || 'http://127.0.0.1:5080').replace(/\/$/, '');
const runId = __ENV.RUN_ID;
if (!runId || !/^[a-zA-Z0-9-]{1,28}$/.test(runId)) throw new Error('Set RUN_ID to a unique 1–28 character run identifier.');
const submissions = new Counter('claim_submissions');
const failed = new Rate('claim_submission_failed');
const latency = new Trend('claim_submission_duration', true);
export const options = {
  scenarios: {
    claims: __ENV.SMOKE === 'true'
      ? { executor: 'shared-iterations', vus: 1, iterations: 3, maxDuration: '30s' }
      : { executor: 'ramping-vus', startVUs: 0, stages: [
          { duration: '30s', target: 5 }, { duration: '30s', target: 10 },
          { duration: '60s', target: 10 }, { duration: '30s', target: 0 },
        ], gracefulRampDown: '15s', gracefulStop: '15s' },
  },
  summaryTrendStats: ['count', 'avg', 'min', 'max', 'p(50)', 'p(95)', 'p(99)'],
  thresholds: { claim_submission_failed: ['rate<0.01'] },
  maxRedirects: 0,
};

export default function () {
  const suffix = `${runId}-${__VU}-${__ITER}`;
  const body = JSON.stringify({ id: `CLM-LD-${suffix}`, providerNpi: '0000000006', memberId: `SYN-LD-${suffix}`,
    serviceDate: '2026-01-20', procedureCodes: ['SYN-LOAD'], amount: 42.50, simulation: 'None' });
  const response = http.post(`${base}/api/claims`, body, {
    headers: { 'Content-Type': 'application/json', 'Idempotency-Key': `SYN-LD-${suffix}` },
    timeout: '10s', tags: { name: 'POST /api/claims' },
  });
  submissions.add(1); latency.add(response.timings.duration);
  const ok = check(response, { 'claim accepted (202)': r => r.status === 202 });
  failed.add(!ok);
  sleep(1);
}

export function handleSummary(data) {
  return { [__ENV.SUMMARY_PATH || 'benchmarks/results/load-raw.json']: JSON.stringify(data, null, 2) + '\n' };
}
