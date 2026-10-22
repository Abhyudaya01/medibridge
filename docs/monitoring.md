# Failure-rate monitoring

`GET /api/monitoring/pcharts` returns computed p-charts from actual persisted
observations. It is read-only and does not send notifications or change claim,
prior-auth or rule states. The response reports its `asOf` time, denominators,
excluded observations and all available series.

## Batch and calibration contract

Batches are aligned to UTC five-minute boundaries. A batch is eligible only when
its end is at or before `asOf`; the current partial batch is excluded. Empty windows
add no artificial zero rates. Each series calibrates on its first **20 completed,
nonempty batches**, ordered by server observation time. It never replaces that
prefix with recent monitoring traffic. Appending later batches does not refit the
baseline. The baseline is computed from the persisted history on each read; a
late commit with an older receipt time can revise that historical batch.

For failures `d_i` among opportunities `n_i`, use the weighted baseline
`p_bar = sum(d_i) / sum(n_i)` over calibration batches, rather than averaging batch
percentages. For each batch, the center is `p_bar` and limits are:

```
LCL_i = max(0, p_bar - 3 * sqrt(p_bar * (1 - p_bar) / n_i))
UCL_i = min(1, p_bar + 3 * sqrt(p_bar * (1 - p_bar) / n_i))
```

This is the [NIST proportions-control-chart method](https://www.itl.nist.gov/div898/software/dataplot/refman1/ch2/pcontrol.pdf).
A point is outside control limits only when its rate is strictly below LCL or above
UCL. Equality is within limits. `outsideLimits` also identifies calibration
excursions; `alert` is true only for an out-of-control **post-calibration** point.
Limits and center remain `null` until calibration is complete.

Zero/one baselines yield degenerate limits and remain visible. The response marks
`normalApproximationReliable=false` when expected failures or passes are below
five. Three-sigma limits then have a less reliable nominal false-alert probability.
The model assumes comparable, approximately independent Bernoulli opportunities;
changes in traffic mix or intended rule policies can also create excursions.
This is statistical monitoring of a synthetic workflow, not clinical risk detection.

The response retains the calibration prefix plus the latest 60 subsequent batches
per series, with an explicit omitted-middle-batch count. Underlying records are not
deleted. Timestamps in the table identify gaps. Rule history streams in timestamp
order, bounding aggregation memory per series; endpoint counts are grouped in SQL.
A monitoring read still scans historical live rule traces, so it is a portfolio
implementation rather than a pre-aggregated large-scale telemetry service.

## Denominators and persistence

- **Rule series:** domain + immutable version + definition hash + rule ID. A
  denominator opportunity means that exact live rule actually executed for a
  committed, protocol-accepted intake evaluation. The numerator counts that rule's
  validation failure, at most once per evaluation. Other rules failing do not make
  this rule fail. Conditional skips, other plan scopes, shadow evaluations and
  idempotent replays add no opportunities. Core returns `evaluatedRuleIds` beside
  the failures; these traces commit in the existing append-only evaluation JSON.
- **Endpoint series:** method + route template for `/api` attempts. Status >=400,
  unhandled exceptions and client aborts count as failed attempts. A 202 intake or
  200 duplicate suppression is a successful HTTP response even if later processing
  requires attention. An idempotent replay is another HTTP attempt. Failures before
  domain evaluation, such as bad JSON or missing idempotency headers, still count.
  Exceptions are represented with a terminal 500 observation; this does not claim
  that a 500 response reached an already-disconnected client.
- Health, static assets and monitoring reads are excluded from HTTP telemetry.
  Unmatched API paths share `/api/unmatched`; query strings, actual path IDs,
  request bodies, headers and credentials are never stored in endpoint telemetry.
- Endpoint observations and their general audit entries commit together in a
  separate scope after request processing. They cannot modify or roll back a
  successful business transaction. A five-second persistence budget and logged
  failures make this **best-effort operational telemetry**: a database outage can
  leave gaps, and the API does not claim to count unrecorded requests.
- SQLite/SQL Server migrations add the endpoint table and time indexes. EF guards
  and database triggers reject updates/deletes. Existing evaluation JSON is never
  rewritten. Missing legacy execution traces, malformed traces and protocol-rejected
  evaluations are explicitly counted in `excludedHistoricalOrInvalidEvaluations`,
  not treated as passes. No retrospective denominator is fabricated.

## Demo

The page loads pinned Chart.js 4.5.1 UMD from cdnjs, with SRI and anonymous CORS.
Select live series by endpoint or rule version, or view the separately labeled
**recorded local drift benchmark** (the default). It uses the exact committed
`drift.json` artifact. Lines show observed rate, center and both limits; red triangle
points identify alerts and gray points identify calibration. An accessible table
contains every count, limit and signal, even when the CDN cannot load.

The recorded Core coverage percentage is likewise read from `coverage.json`.
Neither view runs benchmarks, injects live failures, or writes invented metrics.

## Reproduce the local drift experiment

```sh
dotnet run --project benchmarks/drift -c Release
```

See [the drift harness](../benchmarks/drift/README.md) and
[measured results](../benchmarks/RESULTS.md).
