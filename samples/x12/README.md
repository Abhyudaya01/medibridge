# Synthetic X12 examples

These are fabricated MediBridge subset fixtures. They contain no real member/provider identities, PHI, payer names, or branding. `0000000006` is a fabricated checksum-only NPI; member IDs begin with `SYN-`. Fake procedure/diagnosis codes are intentional. These simplified fixtures are not complete production 837P messages.

| File | Expected first import |
|---|---|
| `valid.837` | Accepted 999-style result; one Succeeded claim |
| `missing-npi.837` | Rejected; billing provider NM109 error; no claim inserted |
| `bad-delimiters.837` | Rejected; ISA component and element delimiter collision; no claim inserted |
| `duplicate-claim.837` | Accepted; first claim Succeeded and second Suppressed for the same identity |
| `multi-claim.837` | Accepted; two distinct claims and subscriber contexts imported atomically |

Reuploading an existing claim ID yields DuplicateId and preserves the original record. Database contents can affect Succeeded/Suppressed outcomes; acknowledgment acceptance continues to describe subset validation. Each file's envelope counts are generated from its actual segments, except the deliberate delimiter corruption in `bad-delimiters.837`.

See [the exact subset contract](../../docs/x12-837p-subset.md) for element mappings and limitations.
