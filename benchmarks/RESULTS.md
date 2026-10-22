# Measured benchmark results

Synthetic data only. Chaos uses a virtual clock; load measures HTTP submission latency, not end-to-end recovery.

| Completed UTC | Harness | Run ID | Status | Target | Measured result | Artifact |
|---|---|---|---|---|---|---|
| 2026-10-21T20:47:49.606489+00:00 | load | 20261021T204746-78105a | completed | http://127.0.0.1:5080 / local / v0.5.0 | 3 requests; 0.9323 req/s; p50/p95/p99 8.244/175.058/189.886 ms; failed 0 (0.000%) | `results/load-smoke.json` |
| 2026-10-21T20:55:57.3734100Z | chaos | 20261021T204536Z-233a9c54 | completed | SQLite / virtual clock | 1000 iterations; 50000 claims; duplicates 0; duplicate effects 0; transient recovery 8918/8930 (99.8656%); business-rule retried 0 | `results/chaos-initial.json` |
| 2026-10-21T21:03:29.4116280Z | chaos | 20261021T205657Z-ff96f4a3 | completed | SQLite / virtual clock | 1000 iterations; 50000 claims; duplicates 0; duplicate effects 0; transient recovery 8918/8930 (99.8656%); business-rule retried 0 | `results/chaos.json` |
| 2026-10-21T22:39:55.388957+00:00 | load | 20261021T223723-e18041 | completed | https://medibridge-abhi.azurewebsites.net / B1 / v0.4.0 | 645 requests; 4.2670 req/s; p50/p95/p99 486.709/1402.892/2198.381 ms; failed 0 (0.000%) | `results/load.json` |
| 2026-10-21T23:24:18.1695120Z | parity | 20261021T232417Z | completed | Local X12 278 / FHIR R4 | 200/200 paired cases passed (100.0000%); canonical 200; outcomes 200; independent expected 200 | `results/parity.json` |
| 2026-10-22T00:12:24.2159750+00:00 | sla | 20261022T001006Z | completed | SQLite / 30-day accelerated arrivals | 10000 requests; missed deadlines 1137; escalations 3164 (at risk 2027, breached 1137); at-risk mean/median lead 31.1764/33.5908 h; oracle 10000/10000 | `results/sla.json` |
| 2026-10-22T14:02:38.4453870+00:00 | generator | 5d34bbaf8fbc492d9813576e65e0cbc8 | completed | Local SQLite / Core validation | 250000 claims; invalid 20000 (8.00%); generation 0.672 s; bulk load 1.698 s; wall 2.622 s | `results/generator.json` |
| 2026-10-22T14:02:42.5135930+00:00 | shadow | 014726f4b090424baf980506afb262ac | completed | Local SQLite / Core validation | 250000 claims; flips 44812/44812; detection 100.0000%; FP 0; FN 0; evaluation + oracle 1.228 s; wall 3.476 s | `results/shadow.json` |
