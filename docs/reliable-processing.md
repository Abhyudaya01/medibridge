# Chunk 1b · reliable synthetic processing

All claims remain fabricated portfolio records. No real PHI, member IDs, payer identities, or external healthcare services. Version 0.4.0 accepts work durably and processes it asynchronously.

## Failure policy and queues

Core `FailureClassifier` classifies Timeout/HTTP 408, HTTP 500–599, SQL timeout -2, deadlock 1205, and Azure SQL errors 40197, 40501, 40613, 10928, 10929, 49918–49920 as Transient. Explicit BusinessRule failures take precedence and are never retried. Unknown/permanent failures are Unclassified and require manual review; they are not mislabeled as business corrections or rule-version conflicts.

`RetryPolicy` allows **five total processing attempts**, including the initial attempt. Following attempts 1–4, delay is `min(60 seconds, 5 × 2^(attempt−1) × (0.5 + jitter))`, with jitter supplied as a value in [0,1]. Core calculations are deterministic; the dispatcher supplies randomness and a `TimeProvider` supplies time. Exhaustion moves the claim to ManualReview and its message to DeadLetter. Receipt-based duplicate deliveries do not consume another processing attempt. A crash after AttemptStarted consumes that reserved attempt even if the handler did not finish.

Business-rule intake failures retain the existing 422 response and an append-only timeline, and create a correction record in the same transaction. `GET /api/queues/correction` lists them. A business-rule failure discovered by a handler also enters this queue on its first failed attempt. This chunk exposes the queue for inspection; it adds no correction/edit/delete endpoint. Invalid transport requests without a persistable claim do not create correction items.

## Request idempotency

Both POST endpoints require one `Idempotency-Key` header containing 1–128 printable ASCII characters without spaces. The UI creates keys automatically and reuses a key when retrying an identical submission within the page session. Keys are scoped to normalized endpoint paths and stored under a unique database index with case-sensitive key comparison.

The fingerprint includes the exact request bytes and Content-Type. A matching replay returns the original HTTP status, body, Content-Type, and Location plus `Idempotency-Replayed: true`, even if the dispatcher has since changed claim state. Reusing a key with different bytes or Content-Type returns 409. Missing/invalid keys return 400. This is request idempotency, distinct from claim-ID replay and business duplicate detection. The stored response is the authoritative submission result, not a fresh status query. Use GET to inspect current processing.

The idempotency row and response commit in the same transaction as claims, correction entries, outbox rows, and related audits. Concurrent matching keys produce one durable submission. A cross-process unique-key race rolls back and re-evaluates once. Responses at or above 500 are not cached. Records are retained; expiry/cleanup is outside this chunk. Raw requests are hashed rather than stored; cached responses contain synthetic claim data.

For X12 uploads, the request transaction now encompasses all accepted interchanges **and the cached response**. Syntax rejection still applies independently to each interchange, but a storage failure rolls back all otherwise accepted interchanges in that HTTP request. This strengthens chunk 1a's previous per-interchange persistence behavior. A 999-style Accepted result still describes syntax and required-field validation; valid new claim outcomes are PendingProcessing, not immediately Succeeded. Duplicate claim outcomes remain Suppressed, and reused claim IDs remain DuplicateId.

## Transactional outbox and crash recovery

Validated claims return 202 with PendingProcessing, and an OutboxMessage is inserted atomically with the claim. Business-rule rejection, suppression, and rule-version review are terminal intake outcomes and do not schedule downstream work. The outbox handler remains a local synthetic adapter; no real external request is made.

The BackgroundService dispatcher acquires a database-backed 30-second lease using an optimistic concurrency token. It records delivery, attempt start, failure, retry scheduling, completion, lease recovery, and duplicate-delivery outcomes in ClaimEvents. Outbox/idempotency/receipt/correction state changes also create append-only Audit records. Both audit tables reject tracked and direct SQL update/delete operations. These controls preserve application history, not a tamper-proof ledger against administrators.

A successful handler writes its claim effect and a unique HandlerReceipt in one transaction. Acknowledgment is a separate outbox transition. If a process dies after the effect commits but before acknowledgment, the lease expires, the message is redelivered, the receipt suppresses the duplicate effect, and the message becomes Completed. An uncertain SQL commit is checked against the receipt before changing claim state. All handler state changes use database transactions; delivery is at-least-once and local handler effects are idempotent.

`AttemptCount` counts reserved processing attempts; `RetryCount` is `max(0, AttemptCount−1)`. Outbox `Deliveries` counts leases, including receipt-suppressed redeliveries. GET `/api/outbox` exposes public synthetic status without lease-owner tokens. The demo shows these counts and both queues; Refresh claims updates them. It does not run a tight browser polling loop.

The worker wakes on committed submissions and known retry/lease deadlines. With no outstanding work, it scans at most once every 65 minutes, rather than continuously polling serverless SQL and preventing its idle pause. Storage unavailability causes a bounded 30-second recovery wait; business processing attempt budgets remain durable. Startup scans recover work after an application restart.

## Fault injector

`IFaultInjector` is injectable in tests. Configuration keys:

| Key | Default | Meaning |
|---|---|---|
| `FaultInjection__Enabled` | false | Explicit opt-in; production and local defaults are off |
| `FaultInjection__TransientFailureRate` | 0 | Probability in [0,1] of a synthetic timeout per attempt |
| `FaultInjection__AddedLatencyMs` | 0 | Added per-attempt latency, 0–10000 ms |
| `FaultInjection__CrashAfterStep` | unset | AfterClaimCommit, AfterLease, AfterAttemptStarted, or AfterHandlerCommit |

A configured crash checkpoint terminates the process with FailFast. Use only isolated synthetic test environments. The process recovery test starts a disposable app/database, crashes after handler commit, restarts without injection, and verifies one effect, a recovered delivery, and an idempotent HTTP replay. Explicit claim `Simulation=DownstreamTimeout` remains a deterministic two-attempt demo scenario and is separate from global random fault injection.

`Outbox__Enabled=false` disables dispatch for controlled tests. `Seed__Enabled=false` avoids initial fixtures in subprocess tests. Normal deployments leave dispatch and seed enabled.

## Migrations and SQL cutover

SQLite remains the default (`Database__Path` override); setting `ConnectionStrings:Sql` selects SQL Server. On App Service the SQLAzure connection string named Sql maps to that configuration key. Separate SqliteClaimsDbContext and SqlClaimsDbContext migration sets share domain/persistence mappings under `Infrastructure/Migrations`.

Startup applies migrations before serving requests. A legacy EnsureCreated database is adopted only after its Claims and ClaimEvents column sets match the known previous schema. SQLite gets a `.legacy-backup.db` snapshot first. Unknown legacy schemas cause startup failure rather than destructive guesses. Existing claims/timelines remain intact; legacy business-rule rejections are backfilled into the correction queue. Historic synchronous claims have no new durable handler attempts or outbox rows fabricated for them.

The Azure SQL cutover uses a fresh database initialized with synthetic seeds; the old `/home/data/medibridge.db` remains preserved. This is not an automatic cross-provider data transfer. Server/database names are public metadata in `infra/azure.env`. Credentials exist only in the App Service Sql connection string, never in source, workflow output, or provisioning output.

`infra/provision-sql.py` checks subscription `36637271-a6f8-4110-9be1-55f112612c8c` before every Azure mutation and explicitly targets it. It reads the region from azure.env, registers Microsoft.Sql if needed, generates an administrator password in memory, and creates General Purpose Gen5 serverless with the free offer and AutoPause on monthly-limit exhaustion. It verifies these safeguards before configuring App Service. The requested Azure-services firewall exception uses 0.0.0.0–0.0.0.0; no workstation/all-Internet firewall rule is added. Azure command file logging and telemetry are disabled for credential-bearing subprocesses, whose output is captured and suppressed. No paid fallback is automatic.

Tests cover both migration sets, legacy preservation, classifier/budget boundaries, correction behavior, concurrent HTTP replay, outbox delivery and receipts, deterministic lease recovery, and real-process restart recovery. CI creates a disposable SQL Server container with a generated masked test-only credential; local runs skip that one integration test when MEDIBRIDGE_TEST_SQL is unset.
