using MediBridge.Core.X12;

namespace MediBridge.Tests;

internal static class X12Samples
{
    internal static string Load(string name = "valid") => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "x12", name + ".837"));
    internal static string Recount(string text)
    {
        var segments = text.Replace("\r", "").Replace("\n", "").Split('~', StringSplitOptions.RemoveEmptyEntries);
        var start = Array.FindIndex(segments, segment => segment.StartsWith("ST*"));
        var end = Array.FindIndex(segments, segment => segment.StartsWith("SE*"));
        if (start >= 0 && end >= start)
        {
            var values = segments[end].Split('*');
            values[1] = (end - start + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            segments[end] = string.Join('*', values);
        }
        return string.Join('~', segments) + '~';
    }
    internal static X12Interchange Parse(string text) => Assert.Single(X12Parser.Parse(text));
}

public class X12ParserTests
{
    [Fact]
    public void ValidSampleMapsAllClaimFieldsAndHiMetadata()
    {
        var result = X12Samples.Parse(X12Samples.Load());
        Assert.True(result.Accepted, string.Join('\n', result.Errors.Select(e => e.Reason)));
        Assert.Equal("000000101", result.ControlNumber);
        var parsed = Assert.Single(result.Claims);
        Assert.Equal("CLM-X12-VALID", parsed.Claim.Id);
        Assert.Equal("0000000006", parsed.Claim.ProviderNpi);
        Assert.Equal("SYN-X12-001", parsed.Claim.MemberId);
        Assert.Equal(new DateOnly(2026, 1, 20), parsed.Claim.ServiceDate);
        Assert.Equal(125.50m, parsed.Claim.Amount);
        Assert.Equal(new[] { "SYNPROC1" }, parsed.Claim.ProcedureCodes);
        Assert.Equal(new[] { "SYN001" }, parsed.DiagnosisCodes);
        Assert.Equal(7, parsed.SegmentPosition);
    }

    [Fact]
    public void TokenizerUsesAllDeclaredDelimitersAndPreservesEmptyElements()
    {
        var input = X12Samples.Load().Replace('*', '|').Replace(':', '>').Replace('^', '!').Replace('~', '$');
        var result = X12Samples.Parse(input);
        Assert.True(result.Accepted);
        Assert.Equal(new X12Delimiters('|', '>', '!', '$'), result.Delimiters);
        var tokens = Assert.Single(X12Tokenizer.Tokenize(input));
        var claim = Assert.Single(tokens.Segments, s => s.Id == "CLM");
        Assert.Equal("", claim.Element(3));
        Assert.Equal("", claim.Element(4));
        Assert.Equal("11>B>1", claim.Element(5));
        var acknowledgment = X12Acknowledgments.Generate(result);
        Assert.Contains("ST|999|0001|005010X231A1$", acknowledgment.Text);
        Assert.DoesNotContain('*', acknowledgment.Text);
    }

    [Fact]
    public void MissingNpiHasExactSegmentAndElementLocation()
    {
        var result = X12Samples.Parse(X12Samples.Load("missing-npi"));
        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.SegmentId == "NM1" && e.SegmentPosition == 5 && e.ElementPosition == 9
            && e.TransactionPosition == 3 && e.GroupControlNumber == "1" && e.TransactionControlNumber == "0001");
        Assert.All(result.Errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ISA*")]
    [InlineData("not an interchange")]
    public void EmptyOrTruncatedInputReturnsRejectedIsaError(string input)
    {
        var result = X12Samples.Parse(input);
        Assert.False(result.Accepted);
        Assert.Equal("ISA", result.Errors[0].SegmentId);
        Assert.Equal(1, result.Errors[0].SegmentPosition);
        Assert.Equal(0, result.Errors[0].ElementPosition);
    }

    [Fact]
    public void DelimiterCollisionAndMalformedIsaWidthsAreRejected()
    {
        var result = X12Samples.Parse(X12Samples.Load("bad-delimiters"));
        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.SegmentId == "ISA" && e.Reason.Contains("distinct"));
        var chars = X12Samples.Load().ToCharArray(); chars[17] = '|';
        result = X12Samples.Parse(new string(chars));
        Assert.Contains(result.Errors, e => e.SegmentId == "ISA" && e.ElementPosition == 2 && e.Reason.Contains("18"));
    }

    [Theory]
    [InlineData("SE*9*0001", "SE*8*0001", "SE", 1)]
    [InlineData("SE*9*0001", "SE*9*9999", "SE", 2)]
    [InlineData("GE*1*1", "GE*2*1", "GE", 1)]
    [InlineData("GE*1*1", "GE*1*2", "GE", 2)]
    [InlineData("IEA*1*000000101", "IEA*2*000000101", "IEA", 1)]
    [InlineData("IEA*1*000000101", "IEA*1*000000999", "IEA", 2)]
    public void EnvelopeControlsAndCountsAreValidated(string original, string replacement, string segment, int element)
    {
        Assert.Contains(original, X12Samples.Load());
        var result = X12Samples.Parse(X12Samples.Load().Replace(original, replacement));
        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.SegmentId == segment && e.ElementPosition == element);
    }

    [Theory]
    [InlineData("NM1*85*2*SYNTHETIC PROVIDER*****XX*0000000006~\n", "CLM")]
    [InlineData("NM1*IL*1*SYNTHETIC*SUBSCRIBER****MI*SYN-X12-001~\n", "CLM")]
    [InlineData("BHT*0019*00*SYN-BATCH*20260120*1200*CH~\n", "NM1")]
    public void RequiredContextCannotBeSkipped(string removed, string expectedSegment)
    {
        var result = X12Samples.Parse(X12Samples.Recount(X12Samples.Load().Replace(removed, "")));
        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.SegmentId == expectedSegment);
    }

    [Fact]
    public void UnsupportedSegmentAndRepetitionsReportSourceLocations()
    {
        var input = X12Samples.Recount(X12Samples.Load().Replace("HI*ABK:SYN001", "NTE*ADD*SYNTHETIC~HI*ABK:SYN001^ABF:SYN002"));
        var result = X12Samples.Parse(input);
        Assert.Contains(result.Errors, e => e.SegmentId == "NTE" && e.ElementPosition == 0);
        Assert.Contains(result.Errors, e => e.SegmentId == "HI" && e.ElementPosition == 1 && e.Reason.Contains("Repeated"));
    }

    [Theory]
    [InlineData("DTP*472*D8*20260120", "DTP*472*D8*20260230", "DTP", 3)]
    [InlineData("DTP*472*D8*20260120", "DTP*472*RD8*20260120", "DTP", 2)]
    [InlineData("CLM-X12-VALID*125.50", "CLM-X12-VALID*126.50", "CLM", 2)]
    [InlineData("HC:SYNPROC1", "XX:SYNPROC1", "SV1", 1)]
    [InlineData("ABK:SYN001", "ABK:", "HI", 1)]
    [InlineData("*125.50*UN*1", "*125.50*UN*0", "SV1", 4)]
    [InlineData("*125.50*UN*1", "*1,25*UN*1", "SV1", 2)]
    public void ElementValidationProducesActionableLocations(string original, string replacement, string segment, int element)
    {
        var result = X12Samples.Parse(X12Samples.Load().Replace(original, replacement));
        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.SegmentId == segment && e.ElementPosition == element && e.Reason.Length > 0);
    }

    [Fact]
    public void CompositeErrorIncludesComponentPosition()
    {
        var result = X12Samples.Parse(X12Samples.Load().Replace("ABK:SYN001", "ABK:TOOLONGCODE"));
        Assert.Contains(result.Errors, e => e.SegmentId == "HI" && e.ElementPosition == 1 && e.ComponentPosition == 2);
    }

    [Fact]
    public void MultipleLinesRequireConsistentDatesAndBalancedCharges()
    {
        var input = X12Samples.Recount(X12Samples.Load().Replace("CLM-X12-VALID*125.50", "CLM-X12-VALID*251.00")
            .Replace("DTP*472*D8*20260120", "DTP*472*D8*20260120~SV1*HC:SYNPROC2*125.50*UN*2~DTP*472*D8*20260120"));
        Assert.True(X12Samples.Parse(input).Accepted);
        Assert.Contains(X12Samples.Parse(input.Replace("SYNPROC2*125.50*UN*2~DTP*472*D8*20260120", "SYNPROC2*125.50*UN*2~DTP*472*D8*20260121")).Errors,
            e => e.SegmentId == "DTP" && e.ElementPosition == 3 && e.Reason.Contains("agree"));
        var missingDate = X12Samples.Recount(input.Replace("SYNPROC1*125.50*UN*1~DTP*472*D8*20260120", "SYNPROC1*125.50*UN*1"));
        Assert.False(X12Samples.Parse(missingDate).Accepted);
    }

    [Fact]
    public void MultiClaimContextAndSeparateInterchangesDoNotLeak()
    {
        var multi = X12Samples.Parse(X12Samples.Load("multi-claim"));
        Assert.True(multi.Accepted);
        Assert.Equal(new[] { "SYN-X12-MULTI-A", "SYN-X12-MULTI-B" }, multi.Claims.Select(c => c.Claim.MemberId));
        Assert.Equal(new[] { "SYNPROC1", "SYNPROC2" }, multi.Claims.Select(c => Assert.Single(c.Claim.ProcedureCodes)));
        var second = X12Samples.Load().Replace('*', '|').Replace(':', '>').Replace('^', '!').Replace('~', '$');
        var results = X12Parser.Parse(X12Samples.Load("missing-npi") + second);
        Assert.Equal(2, results.Count);
        Assert.False(results[0].Accepted);
        Assert.True(results[1].Accepted);
        Assert.Equal('|', results[1].Delimiters.Element);
    }

    [Fact]
    public void MissingTrailersAndTerminatorAreReported()
    {
        var result = X12Samples.Parse(X12Samples.Load().Replace("IEA*1*000000101~", ""));
        Assert.Contains(result.Errors, e => e.SegmentId == "IEA");
        result = X12Samples.Parse(X12Samples.Load().TrimEnd('\n', '~'));
        Assert.Contains(result.Errors, e => e.Reason.Contains("terminator"));
    }

    [Fact]
    public void BadHeaderCanRecoverAtNextPhysicalIsaBoundary()
    {
        var results = X12Parser.Parse(X12Samples.Load("bad-delimiters") + X12Samples.Load());
        Assert.Equal(2, results.Count);
        Assert.False(results[0].Accepted);
        Assert.True(results[1].Accepted);
    }

    [Fact]
    public void AcceptedAcknowledgmentIsDeterministicAndCountsItself()
    {
        var result = X12Samples.Parse(X12Samples.Load());
        var ack = X12Acknowledgments.Generate(result);
        Assert.Equal("Accepted", ack.Status);
        Assert.Empty(ack.Errors);
        Assert.Equal("ST*999*0001*005010X231A1~AK1*HC*1~AK2*837*0001~IK5*A~AK9*A*1*1*1~SE*6*0001~", ack.Text);
        Assert.Equal(ack.Text, X12Acknowledgments.Generate(X12Samples.Parse(X12Samples.Load())).Text);
    }

    [Fact]
    public void RejectedAcknowledgmentIncludesIk3Ik4AndFullReasonList()
    {
        var ack = X12Acknowledgments.Generate(X12Samples.Parse(X12Samples.Load("missing-npi")));
        Assert.Equal("Rejected", ack.Status);
        Assert.Contains("IK3*NM1*3**8~IK4*9**7~", ack.Text);
        Assert.Contains("IK5*R~", ack.Text);
        Assert.Contains("AK9*R*1*1*0~", ack.Text);
        Assert.NotEmpty(ack.Errors);
        var segments = ack.Text.Split('~', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(segments.Length.ToString(), segments[^1].Split('*')[1]);
        var fallback = X12Acknowledgments.Generate(X12Samples.Parse("ISA*"));
        Assert.Null(fallback.InterchangeControlNumber);
        Assert.Contains("AK1*HC*UNKNOWN", fallback.Text);
        Assert.Contains("IK3*ISA*1", fallback.Text);
    }

    [Fact]
    public void MultipleTransactionsAndGroupsHaveSeparateControlsAndAckCounts()
    {
        var input = X12Samples.Load();
        var transaction = input[input.IndexOf("ST*837", StringComparison.Ordinal)..input.IndexOf("GE*1*1", StringComparison.Ordinal)];
        var twoTransactions = input.Replace("GE*1*1", transaction.Replace("*0001", "*0002") + "GE*2*1");
        var result = X12Samples.Parse(twoTransactions);
        Assert.True(result.Accepted);
        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal(2, result.Claims.Count);
        Assert.Contains("AK2*837*0002~", X12Acknowledgments.Generate(result).Text);
        Assert.Contains("AK9*A*2*2*2~", X12Acknowledgments.Generate(result).Text);

        var group = input[input.IndexOf("GS*HC", StringComparison.Ordinal)..input.IndexOf("IEA*1", StringComparison.Ordinal)];
        var twoGroups = input.Replace("IEA*1*000000101", group.Replace("*1*X*", "*2*X*").Replace("GE*1*1", "GE*1*2") + "IEA*2*000000101");
        result = X12Samples.Parse(twoGroups);
        Assert.True(result.Accepted);
        var ack = X12Acknowledgments.Generate(result);
        Assert.Contains("AK1*HC*1~", ack.Text);
        Assert.Contains("AK1*HC*2~", ack.Text);
        Assert.Contains("ST*999*0002*", ack.Text);
        Assert.Contains("SE*6*0002~", ack.Text);
    }

    [Fact]
    public void EmptyGroupAndDuplicateTransactionControlsAreRejected()
    {
        var input = X12Samples.Load();
        var transaction = input[input.IndexOf("ST*837", StringComparison.Ordinal)..input.IndexOf("GE*1*1", StringComparison.Ordinal)];
        var empty = input.Replace(transaction, "").Replace("GE*1*1", "GE*0*1");
        Assert.Contains(X12Samples.Parse(empty).Errors, e => e.SegmentId == "GE" && e.ElementPosition == 1);
        var duplicated = input.Replace("GE*1*1", transaction + "GE*2*1");
        Assert.Contains(X12Samples.Parse(duplicated).Errors, e => e.SegmentId == "ST" && e.ElementPosition == 2);
    }

    [Fact]
    public void UnexpectedScalarComponentsAndProductionIndicatorAreRejected()
    {
        Assert.Contains(X12Samples.Parse(X12Samples.Load().Replace("SYN-BATCH", "SYN:BATCH")).Errors,
            e => e.SegmentId == "BHT" && e.ElementPosition == 3);
        Assert.Contains(X12Samples.Parse(X12Samples.Load().Replace("*0*T*", "*0*P*")).Errors,
            e => e.SegmentId == "ISA" && e.ElementPosition == 15);
    }
}
