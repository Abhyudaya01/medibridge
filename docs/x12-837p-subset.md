# MediBridge synthetic 837P subset · chunk 1a

This is a deterministic portfolio parser for the segment subset below, with **999-style simulation output**. It is not a complete 837P implementation or a production HIPAA compliance validator. Only fabricated test data is permitted. No real PHI, real member IDs, payer names, or branding. Use ISA15 `T`, fabricated provider `0000000006`, and `SYN-` member IDs.

## Transport and framing

`POST /api/x12/837` consumes `text/plain` (UTF-8; charset may be omitted). The server accepts at most 64 KiB, including concatenated interchanges. JSON and other media types/charsets return 415. Invalid UTF-8 returns 400. Empty or malformed X12 receives a rejected acknowledgment with HTTP 422 if every interchange is rejected. HTTP 200 means at least one interchange was accepted; inspect every interchange result and each claim outcome.

The first ISA is exactly 106 ASCII characters including its terminator. The tokenizer reads the element separator at character 4, repetition separator (ISA11) at character 83, component separator (ISA16) at character 105, and segment terminator at character 106. Positions here are **one-based**. It checks every fixed-width element separator. All four delimiters must be distinct printable ASCII punctuation; no whitespace, letters, digits, or control characters. ISA12 is `00501`. Empty elements and fixed-width spaces are preserved. An optional UTF-8 BOM is permitted only at the start of the request.

CR and LF are allowed between segments; they are not stripped from element data. Scalars cannot contain component separators. Repetitions are detected using the ISA11 delimiter and explicitly rejected because this subset does not support repeated element values. Unsupported segments, qualifiers, modifiers, element counts, and date ranges are rejected rather than silently ignored. Full production files that contain HL, SBR, LX, REF, address, or other unlisted segments are outside this chunk's subset.

Valid concatenated interchanges can use different delimiters. A new ISA at a segment boundary ends recovery from an unclosed preceding interchange. If the ISA itself is unreadable, recovery is limited to a subsequent `ISA` immediately following LF. An unreadable header produces an unknown control reference with canonical fallback delimiters `*`, `:`, `^`, `~`; parsing the rest of that damaged interchange is not attempted.

## Supported segment grammar

```text
ISA
  GS
    ST BHT
      NM1(85)
        NM1(IL)
          CLM HI [DTP(claim)] SV1 [DTP(line)] ...
          CLM ...
        NM1(IL) CLM ...
      NM1(85) NM1(IL) CLM ...
    SE
    ST ... SE ...
  GE
  GS ... GE ...
IEA
```

Each group requires at least one transaction, each transaction at least one claim, and each claim at least one service line. Billing-provider and subscriber context resets at each ST. A new billing provider resets subscriber context. A new NM1 or CLM finalizes the previous claim. All dates for a claim must agree because the current Core Claim has one ServiceDate. Claim-level DTP may occur before the first SV1 (before or after HI); alternatively, every SV1 must have its own following DTP. Only one date is allowed per scope. HI occurs exactly once before service lines. Each transaction requires BHT before its NM1/CLM content.

| Segment | Elements and rules | Mapping |
|---|---|---|
| ISA | 16 fixed-width fields; ISA01/03 `00`, ISA05/07 `ZZ`; sender/receiver required; ISA09 valid YYMMDD, ISA10 HHMM, ISA12 `00501`, ISA13 nine digits, ISA14 `0`/`1`, ISA15 `T` | Interchange control and delimiters |
| GS | Exactly 8 fields: `HC`, sender, receiver, YYYYMMDD, HHMM, 1–9 digit unique group control, `X`, `005010X222A1` | Functional-group context |
| ST | Exactly 3 fields: `837`, unique-in-group 4–9 digit control, `005010X222A1` | Transaction context |
| BHT | Exactly 6 fields: `0019`, `00`, required synthetic reference, YYYYMMDD, HHMM, `CH` | Required transaction header; reference not persisted |
| NM1 billing provider | Exactly 9 fields; NM101 `85`, NM102 `2`, NM103 required, NM108 `XX`, NM109 required valid ten-digit NPI (80840-prefixed Luhn) | NM109 → ProviderNpi; names not persisted |
| NM1 subscriber | Exactly 9 fields; NM101 `IL`, NM102 `1`, NM103 required, NM108 `MI`, NM109 `SYN-` member, at most 64 characters | NM109 → MemberId; names not persisted |
| CLM | Exactly 9 fields; CLM01 ID ≤64 ASCII letters/digits/`-`/`_`; CLM02 positive currency; CLM03/04 empty; CLM05 `11:B:1` using declared components; CLM06–09 `Y`, `A`, `Y`, `Y` | Id and Amount |
| HI | 1–12 elements, each two components: first `ABK:code`, subsequent `ABF:code`; fabricated codes 1–8 ASCII alphanumeric characters | DiagnosisCodes in parsed result and X12Intake audit metadata; Core Claim schema unchanged |
| SV1 | Exactly 4 fields: `HC:procedure` (no modifiers), positive line charge, `UN`, positive invariant decimal units; code ≤32 characters; ≤50 lines per claim | ProcedureCodes; line charges must sum to CLM02. SV102 is the extended line charge, not multiplied by units |
| DTP | Exactly 3 fields: `472`, `D8`, valid YYYYMMDD; all dates within one claim must agree | ServiceDate |
| SE | Exactly 2 fields: count from ST through SE inclusive; matching ST02 | Transaction integrity |
| GE | Exactly 2 fields: transaction count and matching GS06 | Group integrity |
| IEA | Exactly 2 fields: group count and matching ISA13 | Interchange integrity |

The existing Core validation rules also run before import. These enforce required fields, code and amount bounds, NPI checksum, and synthetic member identifiers. The parser does not check codes against clinical registries. The samples deliberately use fabricated procedure and diagnosis codes, not an adjudication code set.

## Errors, acknowledgments, and claim processing

Each response contains `interchanges`, with:

- `interchangeControlNumber`, or null when the header cannot be read.
- `status`: Accepted or Rejected for subset syntax/required-field validation.
- `acknowledgment`: status, control reference, 999-style `text`, and full `errors` list.
- `parsedClaims`: mapped Core Claim, HI diagnosis metadata, CLM segment position, and group/transaction controls. On rejection these are diagnostic previews and are **not imported**; they may be incomplete.
- `outcomes`: per-claim Succeeded, Suppressed, or DuplicateId results for accepted interchanges.

An error includes `segmentId`, one-based `segmentPosition` within the interchange (ISA is 1), `elementPosition` (first field after segment ID is 1; 0 means a whole/missing-segment problem), `reason`, optional one-based `componentPosition`, optional `transactionPosition` (ST is 1), and group/transaction controls when known. Expected-but-missing segments use the next segment position or the claim/header whose required context is absent. Errors are emitted deterministically in traversal/validation order; more than one useful error may identify the same field.

The acknowledgment text contains an ST/SE **999-style block for each input functional group**, using declared delimiters: ST(999), AK1, AK2, IK3/IK4 when applicable, IK5, AK9, and SE. It is returned inside the JSON interchange result, not as a production ISA/GS acknowledgment envelope. IK3 uses transaction-relative positions where available; envelope errors use interchange positions. Element/component positions appear in IK4. Reasons are in the accompanying JSON errors list. For this simulation, segment/element notes use fixed generic error codes 7/8; full X12 implementation-guide error-code classification is outside scope. An unreadable header uses group `UNKNOWN`. Ack control numbers are deterministic within the response, and SE counts include ST/SE.

Acceptance is **all-or-nothing per interchange**. Any subset validation error rejects that interchange, all its transaction statuses are R, and AK9 accepted count is zero. This is a documented simulation policy, not a complete 999 acceptance classifier. Other concatenated interchanges are processed independently. Database/infrastructure failures return 500 and roll back the affected interchange; they do not masquerade as a syntax rejection. Earlier committed interchanges remain committed.

A syntactically accepted duplicate gets an **Accepted acknowledgment** and a **Suppressed claim outcome**. Duplicate identity uses the existing provider/member/date/normalized-code rules, including duplicates within the same upload. Reusing a claim ID yields DuplicateId without modifying the original claim or its audit history. Parsed claims with replayed IDs remain previews; the existing record is authoritative. No additional interchange table or raw X12 payload is stored.

Accepted claims and their processing timelines commit atomically in one EF transaction. Each new claim starts with an append-only X12Intake event containing source controls, CLM position, and HI metadata, followed by the normal deterministic processing timeline. Rejected syntax writes no claim state. The existing schema is sufficient, so this chunk does not require a database migration.

## Samples and local request

See [the five samples](../samples/x12/README.md). Example from the repository root:

```sh
curl -i http://localhost:5080/api/x12/837 \
  -H 'Content-Type: text/plain; charset=utf-8' \
  --data-binary @samples/x12/valid.837
```

The demo's Upload X12 panel can read a local .837/.edi/.txt file or pasted text, submit it as text/plain, display parsed claims and error locations, and show the 999-style result. Accepted claims appear in the transaction monitor.

References used for the subset design: [CMS professional claim fields](https://www.cms.gov/Outreach-and-Education/MLN/WBT/MLN4462429-MLN-WBT-1500/1500/lesson04/15/index.html), [X12 delimiter interpretation](https://x12.org/resources/requests-for-interpretation/rfi-2026-delimiter-non-comp-field-0), and [X12 999 control/error-location interpretation](https://x12.org/resources/requests-for-interpretation/rfi-2396-reporting-tcn-mismatch-999). This document defines the narrower MediBridge contract rather than reproducing a licensed implementation guide.
