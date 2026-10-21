# Measured benchmark results

Synthetic data only. Chaos uses a virtual clock; load measures HTTP submission latency, not end-to-end recovery.

| Completed UTC | Harness | Run ID | Status | Target | Measured result | Artifact |
|---|---|---|---|---|---|---|
| 2026-10-21T20:47:49.606489+00:00 | load | 20261021T204746-78105a | completed | http://127.0.0.1:5080 / local / v0.5.0 | 3 requests; 0.9323 req/s; p50/p95/p99 8.244/175.058/189.886 ms; failed 0 (0.000%) | `results/load-smoke.json` |
| 2026-10-21T20:55:57.3734100Z | chaos | 20261021T204536Z-233a9c54 | completed | SQLite / virtual clock | 1000 iterations; 50000 claims; duplicates 0; duplicate effects 0; transient recovery 8918/8930 (99.8656%); business-rule retried 0 | `results/chaos-initial.json` |
| 2026-10-21T21:03:29.4116280Z | chaos | 20261021T205657Z-ff96f4a3 | completed | SQLite / virtual clock | 1000 iterations; 50000 claims; duplicates 0; duplicate effects 0; transient recovery 8918/8930 (99.8656%); business-rule retried 0 | `results/chaos.json` |
| 2026-10-21T22:39:55.388957+00:00 | load | 20261021T223723-e18041 | completed | https://medibridge-abhi.azurewebsites.net / B1 / v0.4.0 | 645 requests; 4.2670 req/s; p50/p95/p99 486.709/1402.892/2198.381 ms; failed 0 (0.000%) | `results/load.json` |
