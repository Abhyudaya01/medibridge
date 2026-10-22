using System.Globalization;
using MediBridge.Core;

namespace MediBridge.Generator;

public sealed record CorpusRow(int Sequence, Claim Claim, string InvalidKind);

// SplitMix64 and integer-only sampling pin the stream independently of System.Random versions.
public sealed class SeededRandom(ulong seed)
{
    private ulong state = seed;
    public ulong Next()
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    public int Below(int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var bound = (ulong)limit; var threshold = unchecked(0UL - bound) % bound;
        ulong value; do { value = Next(); } while (value < threshold);
        return (int)(value % bound);
    }
}

public static class Corpus
{
    public const int DefaultCount = 250000;
    public const ulong DefaultSeed = 837250000;
    public static IEnumerable<CorpusRow> Generate(int count = DefaultCount, ulong seed = DefaultSeed)
    {
        if (count is < 1 or > 1000000) throw new ArgumentOutOfRangeException(nameof(count));
        var rng = new SeededRandom(seed);
        var invalid = new bool[count];
        Array.Fill(invalid, true, 0, count * 8 / 100);
        for (int i = count - 1; i > 0; i--) { var j = rng.Below(i + 1); (invalid[i], invalid[j]) = (invalid[j], invalid[i]); }
        var providers = Enumerable.Range(0, 500).Select(Npi).ToArray();
        for (var i = 0; i < count; i++)
        {
            var provider = rng.Below(100) < 70 ? rng.Below(50) : 50 + rng.Below(450);
            var primary = rng.Below(100) < 70 ? rng.Below(20) : 20 + rng.Below(80);
            var codeRoll = rng.Below(100); var codeCount = codeRoll < 75 ? 1 : codeRoll < 95 ? 2 : 3;
            List<string> codes = [$"SYN-P{primary:D3}"];
            while (codes.Count < codeCount) { var code = $"SYN-P{rng.Below(100):D3}"; if (!codes.Contains(code)) codes.Add(code); }
            var planRoll = rng.Below(100); var plan = planRoll < 65 ? "SYN-BASIC" : planRoll < 95 ? "SYN-PLUS" : "Unspecified";
            var tier = rng.Below(100);
            var cents = tier < 60 ? 3000 + rng.Below(12001) : tier < 85 ? 15001 + rng.Below(35000)
                : tier < 97 ? 50001 + rng.Below(200000) : 250001 + rng.Below(1250000);
            // More recent service dates, concentrated on weekdays in a fixed 180-day window.
            var age = Math.Min(rng.Below(180), rng.Below(180));
            var date = new DateOnly(2026, 1, 1).AddDays(-age);
            if (date.DayOfWeek == DayOfWeek.Saturday) date = date.AddDays(-1);
            if (date.DayOfWeek == DayOfWeek.Sunday) date = date.AddDays(-2);
            var claim = new Claim { Id = $"CLM-GEN-{i + 1:D7}", MemberId = $"SYN-GEN-{i + 1:D7}", ProviderNpi = providers[provider],
                ServiceDate = date, ProcedureCodes = codes, PlanType = plan, Amount = cents / 100m };
            var kind = "None";
            if (invalid[i])
            {
                switch (rng.Below(5))
                {
                    case 0: kind = "MissingNpi"; claim.ProviderNpi = ""; break;
                    case 1: kind = "NpiChecksum"; claim.ProviderNpi = claim.ProviderNpi[..9] + (char)('0' + (claim.ProviderNpi[9] - '0' + 1) % 10); break;
                    case 2: kind = "MissingMember"; claim.MemberId = ""; break;
                    case 3: kind = "MissingCodes"; claim.ProcedureCodes = []; break;
                    case 4: kind = "NonpositiveAmount"; claim.Amount = 0; break;
                }
            }
            yield return new(i + 1, claim, kind);
        }
    }
    // Fabricated providers deliberately start with zero; no real provider registry is used.
    public static string Npi(int provider)
    {
        if (provider is < 0 or >= 500) throw new ArgumentOutOfRangeException(nameof(provider));
        var prefix = provider.ToString("D9", CultureInfo.InvariantCulture);
        var digits = "80840" + prefix; var sum = 0;
        for (int i = digits.Length - 1, position = 0; i >= 0; i--, position++)
        { var n = digits[i] - '0'; if (position % 2 == 0) { n *= 2; if (n > 9) n -= 9; } sum += n; }
        return prefix + ((10 - sum % 10) % 10).ToString(CultureInfo.InvariantCulture);
    }
}

// Independent fixed-contract oracle: does not call RuleEvaluator, RuleCatalog or ClaimRules.
public static class GroundTruth
{
    public static bool V1Accepts(Claim c)
    {
        var npi = "80840" + c.ProviderNpi; var sum = 0;
        var npiOk = c.ProviderNpi.Length == 10 && c.ProviderNpi.All(char.IsAsciiDigit);
        if (npiOk)
            for (int i = npi.Length - 1, p = 0; i >= 0; i--, p++)
            { var n = npi[i] - '0'; if (p % 2 == 1) n = n * 2 > 9 ? n * 2 - 9 : n * 2; sum += n; }
        return !string.IsNullOrWhiteSpace(c.Id) && c.Id.Length <= 64 && npiOk && sum % 10 == 0
            && !string.IsNullOrWhiteSpace(c.MemberId) && c.MemberId.Length <= 64 && c.MemberId.StartsWith("SYN-", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(c.RuleVersion) && c.RuleVersion.Length <= 32 && c.ServiceDate != default
            && c.ProcedureCodes.Count is > 0 and <= 50 && c.ProcedureCodes.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 32)
            && c.Amount > 0 && c.Amount <= 9999999999999999.99m && c.Amount * 100 == decimal.Truncate(c.Amount * 100);
    }
    public static bool V2Accepts(Claim c) => V1Accepts(c) && !(c.PlanType == "SYN-PLUS" && c.Amount > 100m);
    public static bool Flips(Claim c) => V1Accepts(c) != V2Accepts(c);
}
