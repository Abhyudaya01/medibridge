# Synthetic reliability and load measurements

Only fabricated claims are used. Committed numbers come from actual runs, recorded in [RESULTS.md](RESULTS.md) with machine-readable artifacts in `results/`. Failed runs are retained and labelled. Small automated tests write their results to temporary directories rather than the committed ledger.

## Local chaos

```sh
dotnet build -c Release
dotnet run --project benchmarks/chaos -c Release --no-build -- \
  --iterations 1000 --seed 1701 --parallelism 4
```

Defaults are 1,000 iterations, seed 1701, and up to four parallel iterations. Use `--output <json>` and `--results <md>` to keep an exploratory run separate. The default outputs are `benchmarks/results/chaos.json` and `benchmarks/RESULTS.md`. Each iteration has its own temporary SQLite database and 50 unique synthetic claims; five randomly selected claims have missing NPIs. Intake uses the **production ClaimStore**, not HTTP. The test covers persistence and the dispatcher rather than the web server's middleware or network.

A separate `dotnet` child uses the production OutboxDispatcher and a harness IFaultInjector. A SHA-256 draw keyed by seed, claim ID, and attempt injects a timeout with probability 20% **per attempted handler execution**. The measured rate varies; a killed reserved attempt can have no handler execution. The controller selects AfterLease, AfterAttemptStarted, or AfterHandlerCommit and an occurrence from 1–10, waits for that real checkpoint, forcibly kills the child, replays all 50 original IDs, and starts a new child against the same database. The restart drains until every message is Completed or DeadLetter. Each iteration must actually reach its kill checkpoint. The three checkpoints are durable boundaries; this is not random instruction-level corruption or a disk-failure simulation.

The harness-only TimeProvider advances UTC to retry/lease deadlines. Real processes, transactions, constraints, audit events, receipts, retry limits, and crash recovery execute normally. **Wall time and virtual-clock recovery are not production recovery latency or capacity measurements.** No production retry delays or lease lengths are shortened. Parallel iterations share machine resources but never a database.

Metrics are read from the final persisted claims, timelines, receipts, and outbox:

- **Duplicate claims:** extra non-rejected/non-suppressed records sharing the normalized business identity. All 50 IDs must exist exactly once, and all 50 replays must be suppressed by ID.
- **Duplicate handler effects:** extra AttemptSucceeded events for a claim. Receipt count must equal succeeded claims.
- **Transient recovery:** unique claims with an audited transient AttemptFailed event that finish Succeeded, divided by all claims with such an event. Multiple failure events for one claim count once in this denominator. If none were affected, the percentage is null. Terminal exhaustion remains visible as ManualReview; five total attempts still apply.
- **Business-rule failures retried:** claims identified by immutable Validation/Rejected events, regardless of their final category. Initially rejected business-rule claims with any processing attempt. These already failed intake, so even one dispatcher attempt is a violation. Total processing attempts on these claims is also recorded.
- **Pending outbox:** messages still awaiting processing after drain. Integrity violations fail the run and save partial observations; exhaustion into DeadLetter is a valid terminal outcome, not pending work.

Artifacts include the seed, requested/completed iteration counts, parallelism, OS/runtime/CPU metadata, parent commit and dirty-tree flag, a SHA-256 hash of the Core/Infrastructure/API and harness C# sources, crash choice/exit code per iteration, and raw aggregate counts. The source hash identifies uncommitted benchmark code during the initial run; the commit containing the artifact contains that code. Seeded inputs and failure decisions repeat; operating-system timing and the production dispatcher's jitter remain nondeterministic. Temporary databases are removed after measurement; a failed iteration's database is retained under the temporary `medibridge-chaos-<run-id>` folder for diagnosis.

## HTTP load

```sh
# Start the API separately with an isolated local database.
Database__Path=/tmp/medibridge-load.db dotnet run --project src/MediBridge.Api --no-launch-profile --urls http://127.0.0.1:5080
# Three-request local smoke (separate artifact; writes a real ledger row).
python3 benchmarks/load/run.py --smoke --output benchmarks/results/load-smoke.json
# Full profile against the local app.
python3 benchmarks/load/run.py
```

`validate.js` ramps 0→5 VUs over 30s, 5→10 over 30s, holds 10 for 60s, then ramps down over 30s. Each iteration sends one valid synthetic POST with unique claim/member/idempotency identifiers and pauses one second. Requests time out after 10s; graceful stops allow 15s. This is a closed-user workload capped at ten VUs, not a fixed arrival-rate or saturation claim. A run-specific prefix prevents collisions with prior runs. The script requires a run ID if invoked without the runner.

The runner writes the actual k6 custom summary, POST-only p50/p95/p99 in milliseconds, submission requests/sec, request count, failure rate, exact target/version, configuration, k6 version, timestamps, and exit status to `results/load.json`, then appends a ledger row. All POST responses contribute latency, including failures. Success means **HTTP 202 accepted**, not completed downstream processing. A <1% unexpected-response threshold determines k6 success; it is an execution check, not a published latency SLO. DNS/connect/TLS setup is outside k6's request-duration metric; end-to-end recovery and browser performance are outside this test. See [k6 request-duration definitions](https://grafana.com/docs/k6/latest/using-k6/metrics/reference/) and [custom summaries](https://grafana.com/docs/k6/latest/results-output/end-of-test/custom-summary/).

## Approved Azure load window

**Ask for explicit human approval before scaling to billable B1 and generating live load.** The current Azure target comes from `infra/azure.env`; `run.py` refuses non-loopback targets without `--live-approved`. After approval:

```sh
python3 benchmarks/load/azure_run.py --approved
```

The wrapper checks the plan is F1 in the recorded region, checks the required subscription before every mutation, scales to a single B1 worker, verifies the SKU, warms the app, and runs the bounded profile. Its `finally` block restores F1 even when scaling, warmup, or k6 fails, and verifies the resulting SKU. It records the cloud transitions separately in `results/load-azure-operation.json`; load metrics stay in `results/load.json`. A subscription-guard failure or Azure outage can prevent restoration; the wrapper returns failure and reports that explicitly. A forced termination of the controller cannot execute cleanup, so keep the supervised process running through restoration. No SKU fallback, SQL tier change, secret read, or benchmark data deletion is performed. Live synthetic records remain in the demo database and are not included in seed counts.

The committed [live load sample](results/load.json) measured app version **0.4.0** on **B1** before the 0.5.0 benchmark-tooling release. Its [cloud operation record](results/load-azure-operation.json) confirms restoration to **F1**. The exact percentiles, throughput, failures, target, and timestamps are in the artifact and ledger; these observations apply to that recorded workload and environment.

## X12 278 / FHIR R4 parity

```sh
dotnet run --project benchmarks/parity -c Release
```

The parity console generates 200 pairs (160 accepted and 40 shared semantic-invalid cases) and checks both complete canonical models and intake outcomes against independent expected values as well as each other. It writes real measurements to `results/parity.json` and appends a ledger row. Failure retains the artifact and exits nonzero. See [the exact mapping, corpus coverage, and limitations](../docs/prior-auth-intake.md). No external service or live load is used. For an isolated exploratory run, use `--output /tmp/pa-experiment/results/parity.json`; its ledger is written one directory above the output's results directory. `--samples <directory>` regenerates paired synthetic fixtures without running a benchmark.

## Automated checks

`dotnet test` covers aggregate math, null denominators, deterministic failure draws, real child-process crashes, full drain, replay suppression, and JSON/ledger creation. `python3 -m unittest discover -s benchmarks/load -p 'test_*.py'` checks extraction, invalid metrics, append-only ledger rows, subscription guards, and F1 restoration after a simulated load failure. CI runs these short checks, not the 1,000-iteration benchmark or live load.

## SLA backlog simulation

Run `dotnet run --project benchmarks/sla -c Release` for 10,000 requests over 30 simulated arrival days and follow-up until all scheduled decisions finish. The harness advances an injected `IClock` and exercises the production lifecycle processor with SQLite audit persistence. It writes `results/sla.json` and an actual summary row in `RESULTS.md`; no Azure scaling is required.

See [distribution assumptions, measurement definitions, deadline boundaries and the independent oracle](../docs/prior-auth-decisions.md#accelerated-simulation). Reports include missed deadlines, at-risk and breach events, escalation lead time, priority breakdown, and the unresolved day-30 backlog. These distributions are synthetic assumptions; this is a correctness simulation, not an observed healthcare population or throughput benchmark.

## Seeded corpus and shadow replay (chunk 3b)

[Generator assumptions and storage](generator/README.md) and
[shadow oracle and measurement definitions](shadow/README.md) document the local
250,000-claim experiment. From the repository root:

```sh
dotnet run --project benchmarks/generator -c Release
dotnet run --project benchmarks/shadow -c Release
```

Use a fresh `--database` path for regeneration; the loader never overwrites a file.
Measured artifacts are `results/generator.json` and `results/shadow.json`, with ledger
rows in [RESULTS.md](RESULTS.md). The ignored database holds the full corpus, both
rule definitions, per-claim expected/actual verdicts and append-only audits. The
benchmark uses an isolated SQLite schema and the production Core evaluator. It
never sends traffic to Azure and does not measure production API capacity.

## Drift monitoring and Core coverage (chunk 3c)

[Drift](drift/README.md) executes seeded 5%→10% failure-rate changes and stationary
controls through the production p-chart calculator. It reports observed alert
latency, misses and false alerts in `results/drift.json`. [Core coverage](coverage/README.md)
collects Coverlet Cobertura output and validates actual executable-line counts in
`results/coverage.json`. Both append measured rows to [RESULTS.md](RESULTS.md).
They run locally; no Azure scaling or load is involved.
