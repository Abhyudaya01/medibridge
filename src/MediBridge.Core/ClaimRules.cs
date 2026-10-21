using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MediBridge.Core;

public static class ClaimRules
{
    public const string Version = "phase0-v1";

    public static List<ValidationFailure> Validate(Claim claim)
    {
        List<ValidationFailure> failures = [];
        Required(claim.Id, "Id", 64);
        Required(claim.ProviderNpi, "ProviderNpi", 10);
        Required(claim.MemberId, "MemberId", 64);
        Required(claim.RuleVersion, "RuleVersion", 32);
        if (!string.IsNullOrWhiteSpace(claim.ProviderNpi))
        {
            if (claim.ProviderNpi.Length != 10 || claim.ProviderNpi.Any(c => c is < '0' or > '9'))
                failures.Add(new("NPI_FORMAT", "ProviderNpi", "Use exactly 10 ASCII digits for the synthetic NPI."));
            else if (!HasValidNpiChecksum(claim.ProviderNpi))
                failures.Add(new("NPI_CHECKSUM", "ProviderNpi", "Correct the Luhn check digit using the 80840 prefix; demo NPI: 0000000006."));
        }
        if (!string.IsNullOrWhiteSpace(claim.MemberId) && !claim.MemberId.StartsWith("SYN-", StringComparison.Ordinal))
            failures.Add(new("SYNTHETIC_MEMBER", "MemberId", "Use a fabricated member ID beginning with SYN-. Never submit real member data."));
        if (claim.ServiceDate == default)
            failures.Add(new("REQUIRED_SERVICE_DATE", "ServiceDate", "Provide a synthetic service date in YYYY-MM-DD format."));
        if (claim.ProcedureCodes is null || claim.ProcedureCodes.Count == 0 || claim.ProcedureCodes.Any(string.IsNullOrWhiteSpace))
            failures.Add(new("REQUIRED_PROCEDURE_CODES", "ProcedureCodes", "Provide at least one nonempty synthetic procedure code."));
        else if (claim.ProcedureCodes.Count > 50 || claim.ProcedureCodes.Any(c => c.Length > 32))
            failures.Add(new("PROCEDURE_CODES_LIMIT", "ProcedureCodes", "Use at most 50 codes, each no longer than 32 characters."));
        if (claim.Amount <= 0 || claim.Amount > 9999999999999999.99m || decimal.Round(claim.Amount, 2) != claim.Amount)
            failures.Add(new("AMOUNT_RANGE", "Amount", "Provide a positive amount with at most two decimal places and at most 16 integer digits."));
        return failures;

        void Required(string? value, string field, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                failures.Add(new($"REQUIRED_{ToRuleField(field)}", field, $"Provide a synthetic {field} value."));
            else if (value.Length > maxLength)
                failures.Add(new($"LENGTH_{ToRuleField(field)}", field, $"Use at most {maxLength} characters."));
        }
    }

    private static string ToRuleField(string field) => field switch
    {
        "ProviderNpi" => "PROVIDER_NPI", "MemberId" => "MEMBER_ID", "RuleVersion" => "RULE_VERSION", _ => "ID"
    };

    public static bool HasValidNpiChecksum(string? npi)
    {
        if (npi is null || npi.Length != 10 || npi.Any(c => c is < '0' or > '9')) return false;
        var digits = "80840" + npi;
        var total = 0;
        for (var offset = 0; offset < digits.Length; offset++)
        {
            var digit = digits[^(offset + 1)] - '0';
            if (offset % 2 == 1) digit *= 2;
            total += digit / 10 + digit % 10;
        }
        return total % 10 == 0;
    }

    // JSON encoding avoids delimiter ambiguity; codes are a case-insensitive set.
    public static string DuplicateKey(Claim claim)
    {
        var codes = claim.ProcedureCodes.Select(c => c.Trim().ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray();
        var canonical = JsonSerializer.Serialize(new { claim.ProviderNpi, claim.MemberId, claim.ServiceDate, Codes = codes });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool IsDuplicate(Claim claim, IEnumerable<Claim> existing) =>
        existing.Any(other => other.Status is ClaimStatus.Succeeded or ClaimStatus.RetryPending or ClaimStatus.ManualReview
            && DuplicateKey(other) == DuplicateKey(claim));
}
