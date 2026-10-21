using MediBridge.Core;
using System.Text.Json;

namespace MediBridge.Tests;

public class ClaimRuleTests
{
    internal static Claim Valid(string id = "CLM-TEST") => new()
    {
        Id = id, ProviderNpi = "0000000006", MemberId = "SYN-TEST", ServiceDate = new(2026, 1, 20),
        ProcedureCodes = ["SYN-A", "SYN-B"], Amount = 42.50m
    };

    [Fact]
    public void ValidSyntheticClaimPassesEveryRule() => Assert.Empty(ClaimRules.Validate(Valid()));

    [Theory]
    [InlineData("Id", "REQUIRED_ID")]
    [InlineData("ProviderNpi", "REQUIRED_PROVIDER_NPI")]
    [InlineData("MemberId", "REQUIRED_MEMBER_ID")]
    [InlineData("RuleVersion", "REQUIRED_RULE_VERSION")]
    [InlineData("ServiceDate", "REQUIRED_SERVICE_DATE")]
    [InlineData("ProcedureCodes", "REQUIRED_PROCEDURE_CODES")]
    public void RequiredFieldsReturnActionableFailures(string field, string rule)
    {
        var claim = Valid();
        switch (field)
        {
            case "Id": claim.Id = " "; break;
            case "ProviderNpi": claim.ProviderNpi = ""; break;
            case "MemberId": claim.MemberId = ""; break;
            case "RuleVersion": claim.RuleVersion = ""; break;
            case "ServiceDate": claim.ServiceDate = default; break;
            case "ProcedureCodes": claim.ProcedureCodes = []; break;
        }
        var failure = Assert.Single(ClaimRules.Validate(claim), f => f.RuleId == rule);
        Assert.Equal(field, failure.Field);
        Assert.False(string.IsNullOrWhiteSpace(failure.SuggestedFix));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("00000000060")]
    [InlineData("00000A0006")]
    [InlineData("０００００００００６")]
    public void NpiRequiresExactlyTenAsciiDigits(string npi)
    {
        var claim = Valid(); claim.ProviderNpi = npi;
        Assert.Contains(ClaimRules.Validate(claim), f => f.RuleId == "NPI_FORMAT" && f.Field == "ProviderNpi");
    }

    [Theory]
    [InlineData("0000000006", true)]
    [InlineData("0000000000", false)] // A bare Luhn pass must fail once 80840 is included.
    [InlineData("0000000005", false)]
    [InlineData(null, false)]
    public void NpiLuhnIncludesHealthcarePrefix(string? npi, bool expected) => Assert.Equal(expected, ClaimRules.HasValidNpiChecksum(npi));

    [Fact]
    public void WrongCheckDigitReturnsFix()
    {
        var claim = Valid(); claim.ProviderNpi = "0000000000";
        Assert.Contains(ClaimRules.Validate(claim), f => f.RuleId == "NPI_CHECKSUM" && f.SuggestedFix.Contains("80840"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.001)]
    [InlineData(10000000000000000)]
    public void AmountMustFitPositiveCurrency(decimal amount)
    {
        var claim = Valid(); claim.Amount = amount;
        Assert.Contains(ClaimRules.Validate(claim), f => f.RuleId == "AMOUNT_RANGE");
    }

    [Theory]
    [InlineData("Id", "LENGTH_ID")]
    [InlineData("ProviderNpi", "LENGTH_PROVIDER_NPI")]
    [InlineData("MemberId", "LENGTH_MEMBER_ID")]
    [InlineData("RuleVersion", "LENGTH_RULE_VERSION")]
    public void TextLengthsAreBounded(string field, string rule)
    {
        var claim = Valid();
        typeof(Claim).GetProperty(field)!.SetValue(claim, new string('X', 201));
        Assert.Contains(ClaimRules.Validate(claim), f => f.RuleId == rule);
    }

    [Fact]
    public void MemberMustBeExplicitlySynthetic()
    {
        var claim = Valid(); claim.MemberId = "DEMO-UNPREFIXED";
        Assert.Contains(ClaimRules.Validate(claim), f => f.RuleId == "SYNTHETIC_MEMBER");
    }

    [Fact]
    public void ProcedureCodeItemsCannotBeBlankOrOversized()
    {
        var claim = Valid(); claim.ProcedureCodes = [" "];
        Assert.Contains(ClaimRules.Validate(claim), f => f.RuleId == "REQUIRED_PROCEDURE_CODES");
        claim.ProcedureCodes = [new string('X', 33)];
        Assert.Contains(ClaimRules.Validate(claim), f => f.RuleId == "PROCEDURE_CODES_LIMIT");
        claim.ProcedureCodes = Enumerable.Repeat("SYN-A", 51).ToList();
        Assert.Contains(ClaimRules.Validate(claim), f => f.RuleId == "PROCEDURE_CODES_LIMIT");
    }

    [Fact]
    public void DuplicateKeyUsesCodeSetButIgnoresAmountAndId()
    {
        var existing = Valid("CLM-OTHER"); existing.Status = ClaimStatus.Succeeded;
        var candidate = Valid(); candidate.Amount = 99; candidate.ProcedureCodes = [" syn-b ", "syn-a", "SYN-A"];
        Assert.True(ClaimRules.IsDuplicate(candidate, [existing]));
    }

    [Theory]
    [InlineData("ProviderNpi")]
    [InlineData("MemberId")]
    [InlineData("ServiceDate")]
    [InlineData("ProcedureCodes")]
    public void DuplicateIdentityRequiresAllFourComponents(string field)
    {
        var existing = Valid(); existing.Status = ClaimStatus.Succeeded;
        var candidate = Valid("CLM-OTHER");
        switch (field)
        {
            case "ProviderNpi": candidate.ProviderNpi = "0000000014"; break;
            case "MemberId": candidate.MemberId = "SYN-OTHER"; break;
            case "ServiceDate": candidate.ServiceDate = candidate.ServiceDate.AddDays(1); break;
            case "ProcedureCodes": candidate.ProcedureCodes = ["SYN-C"]; break;
        }
        Assert.False(ClaimRules.IsDuplicate(candidate, [existing]));
    }

    [Fact]
    public void RejectedAndSuppressedClaimsDoNotReserveDuplicateIdentity()
    {
        var existing = Valid(); existing.Status = ClaimStatus.Rejected;
        Assert.False(ClaimRules.IsDuplicate(Valid(), [existing]));
        existing.Status = ClaimStatus.Suppressed;
        Assert.False(ClaimRules.IsDuplicate(Valid(), [existing]));
    }

    [Fact]
    public void TimeoutRecoveryIsDeterministicAndRetainsHistory()
    {
        var time = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        var first = ClaimProcessor.Process(Valid(), false, Simulation.DownstreamTimeout, time);
        var second = ClaimProcessor.Process(Valid(), false, Simulation.DownstreamTimeout, time);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(ClaimStatus.Succeeded, first.Claim.Status);
        Assert.Equal(FailureCategory.None, first.Claim.FailureCategory);
        Assert.Equal(new[] { "Timeout", "RetryPending", "Retrying", "Succeeded" }, first.Timeline.TakeLast(4).Select(e => e.Outcome));
        Assert.True(first.Timeline.Zip(first.Timeline.Skip(1)).All(pair => pair.First.Timestamp < pair.Second.Timestamp));
    }

    [Theory]
    [InlineData("rejected", ClaimStatus.Rejected, FailureCategory.BusinessRule)]
    [InlineData("duplicate", ClaimStatus.Suppressed, FailureCategory.Duplicate)]
    [InlineData("version", ClaimStatus.ManualReview, FailureCategory.RuleVersionConflict)]
    public void ProcessorClassifiesExceptions(string scenario, ClaimStatus status, FailureCategory category)
    {
        var claim = Valid();
        if (scenario == "rejected") claim.ProviderNpi = "";
        if (scenario == "version") claim.RuleVersion = "phase0-v0";
        var result = ClaimProcessor.Process(claim, scenario == "duplicate", Simulation.None, new(2026, 1, 20));
        Assert.Equal(status, result.Claim.Status);
        Assert.Equal(category, result.Claim.FailureCategory);
        Assert.All(result.Claim.ValidationFailures, f => Assert.False(string.IsNullOrWhiteSpace(f.SuggestedFix)));
    }
}
