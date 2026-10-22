# Seeded drift detection benchmark

From the repository root:

```sh
dotnet run --project benchmarks/drift -c Release
```

Defaults: seed **3037837**, 100 paired step/stationary trials. Each trial has 20
calibration batches followed by 40 monitored batches, aligned to five-minute UTC
windows. Observations per batch are uniform integers 150–250. Independent seeded
Bernoulli failures use probability **5%** during calibration and **10%** after the
known step. Stationary controls retain 5% throughout. These probabilities are
simulation inputs, not measured healthcare failure rates.

Paired trials use the same random stream; the only changed input is the failure
threshold after calibration. SplitMix64 supplies reproducible draws. The harness
executes the production Core `PCharts` calculator with a count-weighted baseline
and sample-size-specific 3-sigma limits. It does not call HTTP endpoints or measure
database/API capacity, and sends no traffic to Azure.

**Batches until alert** counts the first post-change batch as 1. No alert within 40
monitored batches is censored (`null`), never recorded as zero. Mean/median/p95 delay
include detected trials only; misses and overall detection rate are separately
reported. P95 uses the nearest-rank definition. Calibration excursions do not count
as monitoring alerts. The stationary trials report actual false alerts across all
40 monitored batches per trial; zero false alerts is not an assumed success criterion.

The artifact includes every trial's seed, calibration counts, first alert delay and
alert count. The first step trial includes its full computed chart and batch counts
for the demo. Any trial's complete input can be regenerated from its seed with
`DriftProgram.Generate(seed, step)`. Results include source hashes, environment,
wall runtime, explicit probability inputs and censoring semantics.

CLI options: `--trials 100`, `--seed 3037837`, `--output path.json`. Defaults write
`benchmarks/results/drift.json` and append to `benchmarks/RESULTS.md`. Failed CLI
runs also write an artifact and a failed row. Small automated tests use temporary
output folders and never replace committed results.
