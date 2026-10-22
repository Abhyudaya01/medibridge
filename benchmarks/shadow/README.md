# Local shadow replay and ground truth

First [generate the corpus](../generator/README.md), then run from the repository root:

```sh
dotnet run --project benchmarks/shadow -c Release
```

Defaults: `benchmarks/work/claims.db` and `benchmarks/results/shadow.json`.
Use `--database path.db` and `--output path.json` for alternate runs. The same corpus
can be replayed repeatedly: each run appends observations under a fresh run ID.
All CLI attempts produce a result artifact and ledger row; failures exit nonzero.
The CLI accepts **only local SQLite filenames**, not SQL connection strings or
URLs. It does not read Azure configuration, issue HTTP requests, or use App Service.

The runner pins and verifies both DB-stored rule definitions against their hashes,
the generator manifest and the compiled benchmark contract. It streams inputs in
sequence in batches of at most 1,000, captures an immutable `RuleInput`, and calls
the **production Core `RuleEvaluator`** twice, once per version. Both verdicts and
structured failure lists are saved, alongside independent expected outcomes. Each
observation and its audit entry commit atomically. Input claims are never modified;
shadow verdicts do not become live claim states. This exercises the pure evaluator,
not the application's HTTP shadow orchestration, duplicate detection, dispatcher,
authorization decisions or clinical correctness.

Ground truth uses an independent fixed-contract validator in `GroundTruth`, including
its own Luhn calculation, rather than invoking Core rules or reading v2 clauses.
For each claim:

- v1 accepts if the baseline fields and amount are valid.
- v2 accepts if v1 accepts and the claim is **not** `SYN-PLUS` with amount >100.00.
- A true flip is a difference between those two expected verdicts. Invalid claims
  rejected by both versions do not count as flips, even when their amount exceeds
  the threshold. Generated invalid labels are separately checked against the oracle.

The runner compares identities and both outcomes per claim, then reports true
positives, false positives, false negatives and true negatives. Detection rate is
`TP / (TP + FN) × 100`; precision is `TP / (TP + FP) × 100`. A zero denominator
produces JSON `null`, not a fabricated 100%. Counts are also split by plan.
Tests cover 99.99 / 100.00 / 100.01, other plans, already-invalid claims and negative
controls that always or never declare a flip.

A successful report requires every input read, every observation persisted, the
same ordered corpus fingerprint as generation, matching labels and both oracle
outcomes, the expected flip count, and **zero false positives or negatives**.
Up to 20 mismatches are exported in JSON; the ignored SQLite database retains every
observation. Exact rule and source-content hashes allow the measured code and
corpus to be identified even when the run precedes its result commit.

Runtime uses monotonic `Stopwatch` measurements. `evaluationAndOracleSeconds` includes
deserialization, input capture, **both evaluations**, oracle checks, hashing and
counters. Separate read and persistence phases include SQLite I/O; wall time includes
setup, those phases, verification and source metadata through the measurement
snapshot, excluding the final run-summary insertion and JSON/ledger export. Runs
are single-threaded in Release configuration on the recorded local environment;
these numbers are neither Azure capacity estimates nor HTTP latency measurements.
