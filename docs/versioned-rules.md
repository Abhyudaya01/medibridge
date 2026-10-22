# Versioned synthetic intake validation

Synthetic data only. Not affiliated with any payer. Definitions and outcomes are portfolio simulation policies, not clinical or coverage guidance.

## Storage and evaluation

Intake domain validation for claims and prior authorization uses JSON rule bundles stored in `RuleSets`. Each immutable version has a SHA-256 hash, UTC `effectiveFrom`, optional `effectiveUntil`, and a schema version. `RuleCatalog` and `RuleEvaluator` live in Core and use only the .NET base libraries. They do not execute scripts, dynamic expressions, regular expressions or external calls.

The initial `phase0-v1` bundle preserves the original field requirements, NPI checks, synthetic prefixes, amount limits and PA validation. Its reviewed seed is `src/MediBridge.Core/Rules/baseline.json`, embedded for pure parser/unit/harness use; running intake loads its selected definitions from the database. Startup seeds only an empty registry. Existing rules, claims, PA records and evaluations are not overwritten. Existing records get `Unspecified` plan metadata and no invented historical comparisons.

`RuleControls` holds one optional shadow candidate and an optimistic revision. `RuleActivations` is an append-only promotion history. A receipt pins the control revision, then selects the latest promoted version within that revision whose activation is no later than the server receipt time and whose effective window contains that receipt. Windows are **[from, until)**. Eligibility uses the server `IClock`, not service date or source submission time. A later promotion cannot alter an in-flight snapshot or a saved record.

Promotion is allowed only while the selected version is effective. When a finite activation expires, selection falls back to the latest previous promoted version that is still effective; the initial baseline is open ended. There are no silent replacements of definition content. New JSON claims without `ruleVersion` resolve to the selected version; explicitly supplying a different version retains the existing ManualReview behavior. X12 claims pin the selected version automatically. Historical claims and outbox processing are never revalidated by promotion.

## Supported definition schema

A bundle contains `schemaVersion: 1`, `version` (1–32 ASCII letters/digits/dot/hyphen/underscore), UTC dates, and 1–128 `rules`. Each rule includes:

- `domain`: `Claim` or `PriorAuth`.
- Unique `id` within its domain, `field`, `operator`, and an actionable `fix`.
- Only the parameters supported by that operator, plus optional `when` and `planTypes`.

| Operator | Fields / parameters |
|---|---|
| Required | Text; rejects null, empty or whitespace. |
| MaxLength | Text; required `maxLength`. |
| Digits | Text; required exact `length`, ASCII digits. |
| NpiChecksum | ProviderNpi / RequestingProviderNpi; 10 digits and the 80840-prefix Luhn algorithm. |
| Prefix | Text; required case-sensitive `prefix`. |
| StringShape | Text; optional minLength, maxLength, prefix, alphabet. |
| Allowed | Text; required case-sensitive `values`. |
| CodesRequired | ProcedureCodes / ServiceCodes; nonempty list without blank items. |
| CodesShape | Code lists; optional minLength, maxLength, maxCount, prefix, alphabet. |
| CodesForbidden | Code lists; required values, trimmed and compared ignoring case. |
| Currency | Claim Amount; positive min, bounded max, and scale 0–2. |
| DateRequired | Claim ServiceDate or PA SubmittedAt; non-default date/timestamp. |

Alphabets: `Identifier` permits ASCII letters/digits/dot/hyphen/underscore; `Printable` permits ASCII ! through ~ without spaces. Conditions: `Present` for nonblank text, `Digits10` for checksum prerequisites, and `CodesPresent` for a nonempty list without blank elements. `planTypes` restricts a clause to documented synthetic plan labels.

Claim fields: Id, ProviderNpi, MemberId, RuleVersion, ServiceDate, ProcedureCodes, Amount, PlanType. PA fields: Id, RequestingProviderNpi, MemberId, ServiceCodes, Priority, SubmittedAt, PlanType. Text constraints are bounded at 200 characters; code counts at 50; value lists at 50; fixes at 400 characters. Currency cannot exceed the existing storage range. Unknown fields/operators, ignored or incompatible parameters, duplicate JSON keys (including case variants), extra properties, invalid windows and oversized definitions are rejected. Every bundle retains unconditional PA priority and submitted-time constraints so accepted requests can safely enter the canonical lifecycle.

Protocol grammar, media types, valid encodings, storage identities, monetary wire syntax, and state transitions have their own fixed contracts. 837P parsing separates protocol checks from the selected domain rules, preserving segment/element locations and whole-interchange rejection. PA mappers use the selected bundle while preserving FHIR paths and 278 locations. Arbitrary historical or unparseable content is not converted into a fictitious policy comparison.

## Shadow contract

One candidate may be enabled at a time. Live and candidate evaluate the same immutable snapshot independently, using separate results. A shadow version is evaluated **as if applicable**, including before its effective date, so future changes can be assessed before promotion. It cannot shadow itself when active.

Each newly evaluated mapped claim or PA intake stores one append-only `RuleEvaluations` row with both versions/hashes, live result, optional candidate result, receipt timestamp, source/domain, provider, distinct normalized codes and plan metadata. It commits in the same transaction as claim/PA state and the cached response. Candidate failures never enter live validation failures, 999 acknowledgments, correction queues, duplicate reservations, outbox work, SLA clocks, or decisions. A candidate integrity/evaluation error is stored as `Error`; the live evaluation still determines processing. A live integrity failure fails closed.

For 837P, one comparison is captured per mapped claim. Protocol-rejected mapped claims retain observations with `protocolAccepted=false`, but are excluded from flip calculations. If live domain validation rejects any claim, the interchange remains rejected and no claims are imported. Duplicate existing business IDs are returned through the existing conflict path without a new persisted intake evaluation. Completely unmappable payloads and invalid storage IDs do not create comparisons. Normal semantic rejections with usable identities do create comparisons, including reject→accept observations.

Idempotency replays return the original response and add no evaluations or activations. `X-Synthetic-Plan-Type`, when present, is part of the fingerprint; omitted-header fingerprints remain compatible with pre-0.8.0 responses.

## API

All POST routes require `Idempotency-Key`; rule mutations are atomic and audited. Version names are case-sensitive.

| Route | Behavior |
|---|---|
| GET /api/rules | Active/shadow version, control revision, definitions, hashes, effective dates and current eligibility. |
| POST /api/rules | Register one immutable definition. 201 created; 400 invalid; 409 version exists. |
| POST /api/rules/{version}/shadow | JSON `{"enabled":true,"expectedRevision":0}` enables; false disables that version. Reload the actual revision first. A second candidate or active-version shadow returns 409. |
| GET /api/rules/{version}/shadow-diff | Historical paired validation verdicts for this candidate, including grouped counts. |
| POST /api/rules/{version}/promote | JSON `{"expectedRevision":0}` promotes an effective definition; reload the actual revision first. Promotion of the current candidate disables its shadow mode. |

Unknown versions return 404. Missing mutation fields return 400. Stale/concurrent revisions, future/expired promotion or failed definition integrity return 409. Retrying a conflict after reload uses a new idempotency key. Disable of an already inactive version and promotion of the already active version are no-ops at the matching revision.

`definitionJson` in GET /api/rules preserves the exact JSON decimal text. Prefer it when exporting/editing a definition; JavaScript numeric parsing can round large decimal bounds. The `definition` object is also supplied for structured consumers. Register a **new** version; there is no edit or delete endpoint.

`candidate-v2` is seeded inactive. It caps synthetic claim amounts at 100 and adds a PA review failure for SYN-REVIEW. Enabling shadow and submitting a synthetic amount of 150 demonstrates an accept→reject comparison while live processing continues. The demo never generates comparison counts without an actual submission.

## Grouping and plan metadata

JSON POST /api/claims accepts optional `planType`. Other intake routes accept `X-Synthetic-Plan-Type`. Allowed labels are `Unspecified`, `SYN-BASIC`, `SYN-PLUS`; missing values use Unspecified. They are submitted simulation metadata, not inferred from member IDs, coverage identifiers or payer data. They do not alter duplicate identity or the PA canonical parity model.

The diff endpoint reports:

- `observed`, `compared`, `flipped`, `acceptToReject`, `rejectToAccept`.
- Candidate errors and protocol-rejected observations, excluded from the compared denominator.
- Groups by provider, normalized distinct procedure/service code, and plan type, separated by domain. Each group contains its own compared denominator and both flip directions.
- Baseline versions actually paired with the candidate. Historical pairs remain unchanged after promotions and toggles.

These are intake-validation verdicts, not eventual processing, approval or coverage decisions. A multi-code transaction contributes once to each distinct code group, so code-group totals overlap. Overall totals count each observation once. The demo displays groups with observed flips and labels absent comparisons explicitly.

## Verification

Tests cover baseline compatibility and exact decimal bounds, definition schema rejection, immutable snapshots, both flip directions without live mutation, all four intake formats, malformed X12 exclusion, plan-header replay identity, candidate error isolation, effective-date boundaries and expiration fallback, version conflicts, competing promotions, idempotent mutations and database append-only guards. SQL Server CI additionally exercises the seeded registry, shadow isolation, promotion, and immutable definitions/evaluations against a real server.

Existing benchmark results remain historical measurements of their recorded source revisions. This chunk introduces no new benchmark claim or fabricated traffic count.
