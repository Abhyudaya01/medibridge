# Synthetic prior authorization decision clocks

Portfolio simulation on synthetic data only. Not affiliated with any payer. These configured windows and reason codes are simulation rules, not a statement of legal requirements or clinical/coverage criteria. Decisions are entered by the demo user; the app does not make medical judgments.

## Receipt and deadlines

Only accepted intake enters the clock. `ReceivedAt` comes from the injected Core `IClock`, independent of the source's `SubmittedAt`. Standard requests have a 168-hour window; expedited requests have 72 hours, including weekends. All instants are UTC.

- `OnTrack` before 80% of the window.
- `AtRisk` at or after 80%: 134 hours 24 minutes for standard; 57 hours 36 minutes for expedited.
- `Breached` at or after 100%. A final decision exactly at the deadline counts as missed.

`PriorAuthSlaWorker` polls every 30 real seconds and invokes `PriorAuthLifecycleStore.SweepAsync`. It processes indexed batches of 200. `IClock` provides decision and scan time; production uses an adapter around the existing `TimeProvider.System`. Tests and the standalone SLA simulation inject manually advanced clocks. No public clock-changing endpoint exists. Set `PriorAuthSla:Enabled=false` to disable the worker locally; startup enrollment still runs.

Pending does not pause, reset or extend the clock. Final decisions stop further escalation. Historical SLA state remains visible on the request after finalization; finalized requests leave the active board. The board shows persisted worker state, which can lag time by a polling interval or longer during downtime. A valid late decision applies due escalations in its transaction before recording the final decision, even if the worker has not scanned yet.

A scan after downtime appends both overdue thresholds in order if necessary. Event timestamps record **actual emission**, not a backdated threshold time. `AtRiskAt` and `DueAt` remain in each structured audit snapshot, so late warnings and negative lead time remain measurable. Optimistic version checks make a projection change and its events atomic; competing sweepers reload and retry. A competing decision returns a conflict for the caller to reload and retry.

Existing accepted 0.6.0 records enroll at startup using the first original `Intake/Received` event and canonical priority. This appends a `SlaClock/Started` snapshot without changing old audit events. Overdue thresholds then catch up. Rejected intake stays outside SLA and decision metrics. Additive migrations exist for SQLite and SQL Server; `PriorAuthEvents` remains append-only at the EF and database levels.

## Decision API

`POST /api/pa/{id}/decisions`, `Content-Type: application/json`, with required `Idempotency-Key`:

```json
{"decision":"Denied","reasonCode":"MissingDocumentation"}
```

Decision values are exact, case-sensitive strings: `Approved`, `Denied`, `Pended`. An open request may receive any of these; a pended request may receive Approved or Denied. Approved and Denied are terminal. Pended is not a finalized decision and has no `DecidedAt`.

Defined simulation denial reasons (also `GET /api/pa/denial-reasons`):

| Code | Simulation meaning |
|---|---|
| MissingDocumentation | Required fabricated review documentation is absent. |
| ServiceNotSupported | Service is outside the demo's supported scope. |
| DuplicateRequest | A reviewer identified a duplicate request. |
| SyntheticPolicyNotMet | A defined simulation review criterion was not met. |

Denied requires one code; Approved and Pended reject a supplied non-null code. Arbitrary free text and numeric enum values are not accepted. Invalid transitions append no PA event. HTTP 200 returns canonical intake, lifecycle, and full timeline; 404 means unknown ID; 409 means rejected intake, terminal/repeated transition, clock before receipt, or concurrent change; 422 means unsupported decision/reason. Idempotency replays return the originally committed response without new decision events; a changed payload with the same key returns 409. Decision scopes preserve case-sensitive IDs and are bounded for SQL Server.

`GET /api/pa/{id}` now includes `lifecycle` (null for rejected intake). `outcome` still describes intake validation separately from `lifecycle.decision`.

`GET /api/pa?limit=100&after=PA-ID` lists active requests in ordinal ID order, overall counts for OnTrack/AtRisk/Breached, `asOf`, and the next cursor. Limit is 1–500. Counts cover all active requests rather than just the page.

## Audit-derived metrics

`GET /api/reports/prior-auth-metrics` reads structured snapshots in the append-only `PriorAuthEvents` audit log, ordered by event ID; it does not use mutable lifecycle projections. It reduces each accepted request to its latest snapshot, including legacy enrollment, and returns overall, Standard and Expedited rows:

- Received, Open, Pended, Approved, Denied and Finalized counts.
- Approval rate = Approved / Finalized; denial rate = Denied / Finalized. Values are fractions 0–1, not percentages.
- Mean and median elapsed hours from receipt to the final decision. Pending time and time beyond the deadline are included; rejected intake is excluded. Median averages the two middle values for an even count.
- Rates and turnaround are null when no finalized request exists; counts remain zero.

The endpoint reports all recorded history, without a date filter. `asOf` is the response clock, not an assertion of a transactionally synchronized snapshot with the separate board endpoint.

## Accelerated simulation

```sh
dotnet run --project benchmarks/sla -c Release
```

Default: 10,000 fabricated accepted requests arriving during a 30-day window starting 2026-01-01 UTC; seed 278837. Every submission, pending/final decision and escalation uses a temporary SQLite database and the production lifecycle store. The harness advances `IClock` by one simulated minute per scan and drains decisions beyond day 30; it separately reports the unresolved backlog at the cutoff. It does not run on Azure or alter App Service resources.

Input distributions are **documented synthetic workload assumptions**, not estimates from healthcare data:

- Weighted arrivals: weekdays 09:00–17:00 UTC weight 1, other weekday hours 0.2, weekends 0.15.
- Independent 15% expedited, 20% pended, 75% approved final decisions; remaining finals are denied using the defined codes.
- Baseline lognormal review delay: median 48 hours standard / 20 hours expedited, log-space sigma 0.8, clamped to 1–480 / 1–240 hours respectively.
- Pending adds uniformly distributed 48–144 standard / 12–48 expedited hours. Pending happens after 35% of baseline delay, capped at 12 hours.
- Exact receipt seconds are retained. Pending and final decisions commit at the next minute boundary. At equal timestamps, scans precede decisions, matching the inclusive deadline definition.

The independently calculated oracle checks each final state and exact deadline, final timestamp, reason, and expected escalation counts/times. It does not call production deadline/escalation helpers. Output `benchmarks/results/sla.json` includes input seed/parameters, source commit and content hash, runtime, per-request observations, missed deadlines, escalation counts, at-risk lead time (deadline minus actual emission), priority breakdown, cutoff backlog, and metrics. `benchmarks/RESULTS.md` gets a measured summary row. Nonmatching results exit nonzero. Wall time measures this particular local simulation, not production capacity or actual escalation latency.

Optional `--count`, `--seed`, and `--output` support isolated smoke runs. Their results must not be presented as the default 10,000-request run. Default output and summary must be committed after executing the harness.
