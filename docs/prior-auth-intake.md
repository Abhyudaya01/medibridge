# Chunk 2a: synthetic prior-authorization intake

Only fabricated portfolio records are supported. Synthetic data. Not affiliated with any payer. No clinical, coverage, medical-necessity, or authorization decision is made. **Accepted** means the request was mapped and passed this intake subset. These endpoints are not a complete X12 implementation, FHIR server, PAS `$submit` operation, or certification of PAS profile conformance.

## Canonical contract

`Core/PriorAuth/PriorAuthRequest` contains `id`, `memberId`, `requestingProviderNpi`, `serviceCodes`, `priority` (Standard/Expedited), and `submittedAt`. Core has no FHIR, ASP.NET, or EF dependency. Infrastructure pins `Hl7.Fhir.R4` 6.4.0 and uses its R4 POCO deserializer before projecting FHIR fields into Core.

Identifiers are trimmed, with case preserved. IDs are 1–64 ASCII letters/digits/dots/hyphens/underscores and compare case-sensitively on both database providers. Member IDs must begin with `SYN-` and fit 64 printable ASCII characters without spaces. Providers require a 10-digit ASCII NPI with the 80840-prefix Luhn checksum. Samples use the fabricated checksum-only value `0000000006`; no provider registry is queried. Service codes are synthetic `SYN-` tokens, normalized to uppercase, deduplicated, sorted ordinally, and limited to 50 distinct codes of at most 32 characters. These codes are not real clinical terminology entries.

Submitted timestamps come from the source request, never the receipt clock. Full second precision is required; fractional seconds and date-only values are outside the subset. FHIR requires an explicit offset and is normalized to UTC. BHT has no timezone field, so this simulation interprets its date/time as UTC. Receipt time is recorded separately in the timeline. A mapped but invalid candidate can have null priority/submitted time and is returned with errors; it is never accepted by defaulting those fields.

| Canonical field | X12 278 | FHIR R4 Claim |
|---|---|---|
| id | BHT03 | One `identifier` under `urn:medibridge:prior-auth` |
| memberId | Subscriber NM1 IL, NM109 | `patient.identifier`, or contained Patient identifier, under `urn:medibridge:synthetic-member` |
| requestingProviderNpi | Requester NM1 1P, NM109 | `provider.identifier`, or contained Organization/Practitioner identifier, under `http://hl7.org/fhir/sid/us-npi` |
| serviceCodes | One SV101 HC component per service HL | `item.productOrService.coding` under `urn:medibridge:synthetic-service` |
| priority | UM06: omitted/E → Standard, U → Expedited | PAS level-of-service extension: omitted/E → Standard, U → Expedited |
| submittedAt | BHT04 `yyyyMMdd` + BHT05 `HHmmss`, UTC assumption | `created`, complete dateTime with seconds and UTC offset |

The priority mapping follows [X12's UM06 interpretation](https://x12.org/resources/requests-for-interpretation/rfi-2740-how-expedited-prior-authorization-indicated-278) and the [PAS level-of-service extension](https://hl7.org/fhir/us/davinci-pas/2.2.1/en/StructureDefinition-extension-levelOfServiceCode.html). Emergency `03` and other codes are rejected in this two-priority simulation. The mapping does not define or promise any regulatory turnaround time.

## X12 grammar

Content type is `text/plain` with optional UTF-8 charset. Exactly one interchange, group, ST transaction, subscriber, and patient event is supported. ISA declares the element, component, repetition, and segment delimiters; all must be distinct printable ASCII punctuation. The existing ISA tokenizer enforces the fixed-width header, terminators, and printable ASCII. CR/LF is allowed between segments.

Order:

1. ISA (`ISA12=00501`, nine-digit ISA13, `ISA15=T`), GS (`HI`, `005010X217`), ST (`278`, numeric control, `005010X217`), BHT (`0007`, `13`, request ID, UTC date, UTC time).
2. HL `1` (20, no parent, child=1), NM1 X3/2 with PI identifier `SYN-SIMULATOR`.
3. HL `2` (21, parent 1, child=1), NM1 1P/2 with XX NPI.
4. HL `3` (22, parent 2, child=1), NM1 IL/1 with MI synthetic member ID.
5. HL `4` (EV, parent 3, child=1), UM (`HS`, `I`, `30`, empty UM04/05, optional level UM06).
6. 1–50 service loops: sequential HL IDs starting at 5 (SS, parent 4, child=0), each followed by one SV1 with only its HC:<synthetic service code> composite.
7. SE, GE, IEA. SE counts ST through SE inclusively; ST/SE, GS/GE, and ISA/IEA controls must match. One transaction/group is required.

Names and demographic fields NM103–NM107 are empty. Additional segments/elements, multiple events/interchanges, missing hierarchy, and unsupported service qualifiers are rejected. The HC qualifier is used only as the structural service-code slot in fabricated samples; SYN- values are not actual HCPCS/CPT codes and would not constitute valid production terminology. No 278 response or 999 is generated for PA; endpoints return the common intake response and errors. The existing 837 endpoint retains its 999-style acknowledgment.

## FHIR subset

Content type is `application/fhir+json` or `application/json`, with optional UTF-8 charset. Submit a **single Claim resource**, not a Bundle. Use `status=active`, `use=preauthorization`, type `professional` from `http://terminology.hl7.org/CodeSystem/claim-type`, and processing priority `normal` from `http://terminology.hl7.org/CodeSystem/processpriority`.

Requested urgency is separate from generic processing priority: optional extension URL `http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-levelOfServiceCode`, with one valueCodeableConcept coding from `https://codesystem.x12.org/005010/1338`, code `E` or `U`. Omission means Standard. This is the PAS-shaped urgency representation, not an invented `expedited` FHIR process-priority code.

Patient/provider references must contain one logical identifier or point to a contained resource of the appropriate type. If both are present, they must agree. Contained resources carry only resourceType, id and identifier; IDs are distinct. No external resources are fetched. One focal insurance entry (sequence 1, focal true) carries a logical `SYN-` coverage identifier under `urn:medibridge:synthetic-coverage`. Coverage is retained only as a required transport check, not a coverage determination. Items have distinct positive sequences and a single synthetic service coding each.

The mapped projection intentionally excludes monetary, clinical, diagnosis and demographic content. Unsupported root/contained elements, modifier extensions, unsupported urgency extensions, ambiguous identifiers/codings/references, duplicate JSON properties, invalid SDK structures, and excessive nesting are rejected. Standard optional metadata that is accepted by the R4 parser is not persisted as canonical data. The SDK checks resource parsing/structure; these explicit subset checks and Core rules are not full PAS StructureDefinition/terminology validation. See [FHIR R4 Claim](https://hl7.org/fhir/R4/claim.html), [PAS Claim](https://hl7.org/fhir/us/davinci-pas/en/StructureDefinition-profile-claim-definitions.html), and [Firely SDK](https://www.nuget.org/packages/Hl7.Fhir.R4/6.4.0).

## API, persistence, and audit

Both POST routes require `Idempotency-Key` and share the existing exact-byte/content-type fingerprint and atomic response cache. Inputs are capped at 64 KiB. Matching replay returns the original response/status/Location and creates no extra request/timeline. Changed bytes with the same scoped key return 409. Request IDs are globally unique across formats; use GET or a new synthetic ID after a separate submission already recorded that ID. Posting the same ID with a new key returns 409 and preserves history.

| Route | Result |
|---|---|
| POST `/api/pa/x12-278` | 201 Accepted intake, 422 mapped business-rule rejection, 400 unparseable/unmappable or invalid ID |
| POST `/api/pa/fhir` | Same common response/status contract |
| GET `/api/pa/{id}` | Canonical request, outcome, validation errors and ordered timeline; 404 if missing |

Common response: `{request, outcome, errors, timeline}`. A structural/transport rejection has no persisted PA request; its mapping response can contain `request:null`. Content-type/encoding/size errors return 415/400/413. Errors contain stable rule ID, canonical field, reason, plus X12 segment/element position or FHIR path. Source-specific error paths are not part of canonical equality.

PA request snapshots and their three-stage Received → Mapped → Accepted/Rejected timeline commit atomically with the idempotency response. Identifiable mapped business-rule rejections are retained. SQLite and SQL Server have provider-specific additive migrations; existing claim/outbox/audit data is preserved. Every PA state write requires appended PA events and also creates a generic Audit record. PA events reject tracked and direct SQL updates/deletes; request deletion is forbidden. Raw payloads are not stored. Accepted PA intake is not dispatched to the claim outbox or correction queue; this chunk defines intake only.

## Samples, demo, and parity

[Paired samples](../samples/prior-auth/) use the same ID and canonical content. Choose one on a fresh database, or change the ID before submitting the other format. The demo's sample loader generates a new request ID each time; it displays canonical JSON, source errors and timeline, and can reload a saved request by ID. It reuses idempotency keys for identical retries in the page session.

```sh
curl http://localhost:5080/api/pa/x12-278 -H 'Content-Type: text/plain' \
  -H 'Idempotency-Key: SYN-PA-X12-1' --data-binary @samples/prior-auth/pa-x12-278.278
curl http://localhost:5080/api/pa/PA-SYN-SAMPLE
```

Run `dotnet run --project benchmarks/parity -c Release` from the repository root. The corpus contains 200 pairs: 160 valid and 40 shared semantic-invalid cases (member, checksum, service prefix, unsupported priority). It covers normal/expedited, absent/elective urgency, duplicate/reordered/case-varied codes, both delimiter sets, logical/contained FHIR references and different source timezone offsets. Each mapper's full canonical candidate, outcome, and shared rule IDs must match an independently constructed expected case as well as each other. Rejecting everything or returning null models cannot pass the oracle. This is mapper/shared-intake-validation parity; it does not claim full-standard coverage or compare source-specific timelines, structural diagnostics, transport HTTP statuses, or medical decisions. Endpoint/persistence/migration behavior is tested separately on SQLite and CI SQL Server.

The harness writes `benchmarks/results/parity.json` with measured counts, per-pair observations, input hashes, environment/provenance, and mismatch details, then appends a row to `benchmarks/RESULTS.md`. A mismatch returns nonzero but retains the real measurements. Tests use isolated output directories. No live load or plan scaling is needed for this benchmark.
