# Seeded synthetic claim corpus

Run from the repository root with the installed .NET 10 SDK:

```sh
dotnet run --project benchmarks/generator -c Release
```

Defaults: **250,000 claims**, seed **837250000**, isolated database
`benchmarks/work/claims.db`, measured artifact `benchmarks/results/generator.json`.
The database is ignored by Git. Generation refuses to overwrite any existing file.
For another run, choose a new local path with `--database benchmarks/work/claims-2.db`
and a new artifact with `--output benchmarks/results/generator-2.json`.
`--count` (1–1,000,000) and `--seed` (unsigned 64-bit integer) support small experiments.
Every CLI run writes a JSON artifact and appends a ledger row, including failed runs.

These distributions model workload shape for a portfolio simulation. They are
**assumptions, not estimates derived from clinical or payer data**:

| Dimension | Construction |
|---|---|
| Providers | 500 fabricated, checksum-valid NPIs beginning with zero. 70% of selections use the first 50; 30% use the remaining 450. No registry lookup. |
| Procedures | 100 fabricated `SYN-P000`–`SYN-P099` codes. Primary codes: 70% from the first 20, 30% from the rest. Additional distinct codes are uniform. |
| Codes per claim | 1 / 2 / 3 with probabilities 75% / 20% / 5%, before invalid mutation. |
| Plan types | `SYN-BASIC` 65%, `SYN-PLUS` 30%, `Unspecified` 5%. These are fabricated labels. |
| Amounts | Integer cents; 60% 30.00–150.00, 25% 150.01–500.00, 12% 500.01–2500.00, 3% 2500.01–15000.00. Inclusive uniform sampling within each tier creates a right tail. |
| Service dates | Fixed reference 2026-01-01 minus the smaller of two uniform ages 0–179 days; weekend dates move to Friday. Recent weekdays receive more weight. |
| Members / IDs | Unique `SYN-GEN-` members and `CLM-GEN-` claim IDs before invalid mutation. No real identities. |
| Invalid inputs | Exactly `floor(count × .08)` randomly shuffled positions. Five equally likely mutations: missing NPI, wrong NPI checksum, missing member, missing codes, zero amount. Other fields retain their sampled distribution. |

SplitMix64, rejection sampling and integer arithmetic define the random stream;
there is no dependence on `System.Random` or wall-clock time. Identical seed/count
and source produce identical ordered claim JSON and **corpus SHA-256**, while run
IDs, audit timestamps and timings naturally differ. The fingerprint frames sequence,
invalid-kind label and compact claim JSON with tabs and a newline. JSON strings
escape embedded control characters.

The loader uses `Microsoft.Data.Sqlite` prepared inserts and transactions of at most
1,000 claims, WAL and `synchronous=NORMAL` (durability is not disabled). Each claim
and its append-only audit entry commit together. Rule definitions, the final
manifest, replay observations and run summaries also receive audit entries in their
own transaction. All benchmark tables reject updates and deletes. The schema is
purpose-built for immutable replay inputs; it does not populate the application's
claim store, execute outbox handlers, or measure HTTP intake capacity.

The database contains the unchanged Core `phase0-v1` JSON and the committed
`benchmark-v2.json`. This distinct benchmark candidate adds just one rule:
`BENCH_PLUS_AMOUNT`, rejecting `SYN-PLUS` amounts greater than 100.00. It does not
change the production registry's `candidate-v2`, activate a live rule, or contact
Azure. The manifest records the exact definition hashes, corpus hash, actual
observed distributions, independent validity-label checks, expected flips,
environment, scoped source-content hash, generation and bulk-load timings.

Generation time includes sampling, serialization, hashing, distribution counters
and independent oracle checks. Bulk-load time includes batched claim/audit inserts
and commits. Wall time includes setup and source metadata collection through the
measurement snapshot; final manifest insertion and JSON/ledger export are excluded.
No throughput claim should be inferred for the API from these measurements.
