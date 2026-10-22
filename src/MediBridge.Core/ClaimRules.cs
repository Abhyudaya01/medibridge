using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MediBridge.Core;

public static class ClaimRules
{
    public const string Version = "phase0-v1";

    public static List<ValidationFailure> Validate(Claim claim) =>
        Rules.RuleEvaluator.Evaluate(Rules.RuleCatalog.Baseline, Rules.RuleInput.From(claim)).Failures.ToList();

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
        existing.Any(other => other.Status is ClaimStatus.Succeeded or ClaimStatus.RetryPending or ClaimStatus.ManualReview or ClaimStatus.PendingProcessing or ClaimStatus.Processing
            && DuplicateKey(other) == DuplicateKey(claim));
}
