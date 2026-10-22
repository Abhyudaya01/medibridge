using System.Text.Json;
using MediBridge.Core;
using MediBridge.Core.Rules;
using MediBridge.Generator;
using MediBridge.Shadow;
using Microsoft.Data.Sqlite;

namespace MediBridge.Tests;

public class ShadowBenchmarkTests
{
    [Fact]
    public void CorpusIsReproducibleWithDocumentedDistributionsAndIndependentValidityLabels()
    {
        var rows = Corpus.Generate(10000).ToArray();
        string Encode(IEnumerable<CorpusRow> values) => JsonSerializer.Serialize(values, BenchmarkFiles.Compact);
        Assert.Equal(Encode(rows), Encode(Corpus.Generate(10000)));
        Assert.NotEqual(Encode(rows), Encode(Corpus.Generate(10000, 1)));
        Assert.Equal(10000, rows.Select(r => r.Claim.Id).Distinct().Count());
        Assert.Equal(800, rows.Count(r => r.InvalidKind != "None"));
        Assert.Equal(6, rows.Select(r => r.InvalidKind).Distinct().Count());
        Assert.All(rows, row =>
        {
            Assert.Equal(row.InvalidKind == "None", GroundTruth.V1Accepts(row.Claim));
            Assert.Equal(GroundTruth.V1Accepts(row.Claim), RuleEvaluator.Evaluate(RuleCatalog.Baseline, RuleInput.From(row.Claim)).Outcome == "Accepted");
            Assert.StartsWith("CLM-GEN-", row.Claim.Id);
            if (row.Claim.ProviderNpi.Length > 0) Assert.StartsWith("0", row.Claim.ProviderNpi);
            if (row.Claim.MemberId.Length > 0) Assert.StartsWith("SYN-", row.Claim.MemberId);
            Assert.All(row.Claim.ProcedureCodes, code => Assert.StartsWith("SYN-", code));
            Assert.InRange(row.Claim.ServiceDate, new DateOnly(2025, 7, 4), new DateOnly(2026, 1, 1));
            Assert.DoesNotContain(row.Claim.ServiceDate.DayOfWeek, new[] { DayOfWeek.Saturday, DayOfWeek.Sunday });
        });
        Assert.InRange(rows.Count(r => r.Claim.PlanType == "SYN-BASIC"), 6200, 6800);
        Assert.InRange(rows.Count(r => r.Claim.PlanType == "SYN-PLUS"), 2700, 3300);
        Assert.Contains(rows, r => r.Claim.ProcedureCodes.Count == 3);
        Assert.Contains(rows, r => r.Claim.Amount > 2500m);
        Assert.All(Enumerable.Range(0, 500), i => Assert.True(ClaimRules.HasValidNpiChecksum(Corpus.Npi(i))));
    }

    [Theory]
    [InlineData("SYN-PLUS", "99.99", false, false)]
    [InlineData("SYN-PLUS", "100.00", false, false)]
    [InlineData("SYN-PLUS", "100.01", false, true)]
    [InlineData("SYN-PLUS", "150.00", true, false)]
    [InlineData("SYN-BASIC", "150.00", false, false)]
    [InlineData("Unspecified", "150.00", false, false)]
    [InlineData("SYN-PLUS", "0.00", false, false)]
    public void IndependentOracleAndEvaluatorAgreeAtThresholdAndOnMaskedInvalidClaims(string plan, string amount, bool missingNpi, bool flip)
    {
        var c = Corpus.Generate(1).Single().Claim;
        c.PlanType = plan; c.Amount = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        if (missingNpi) c.ProviderNpi = "";
        var v1 = RuleEvaluator.Evaluate(RuleCatalog.Baseline, RuleInput.From(c));
        var v2 = RuleEvaluator.Evaluate(RuleCatalog.Parse(BenchmarkFiles.V2Json), RuleInput.From(c));
        Assert.Equal(GroundTruth.V1Accepts(c), v1.Outcome == "Accepted");
        Assert.Equal(GroundTruth.V2Accepts(c), v2.Outcome == "Accepted");
        Assert.Equal(flip, GroundTruth.Flips(c)); Assert.Equal(flip, v1.Outcome != v2.Outcome);
    }

    [Fact]
    public void V2KeepsEveryBaselineRuleAndAddsOnlyTheDocumentedPlanSpecificAmountLimit()
    {
        var v2 = RuleCatalog.Parse(BenchmarkFiles.V2Json);
        Assert.Equal("benchmark-v2", v2.Version);
        Assert.Equal(RuleCatalog.Baseline.Rules.Count + 1, v2.Rules.Count);
        var extra = Assert.Single(v2.Rules, r => r.Id == "BENCH_PLUS_AMOUNT");
        Assert.Equal("Claim", extra.Domain); Assert.Equal("Currency", extra.Operator);
        Assert.Equal("Amount", extra.Field); Assert.Equal(100m, extra.Max); Assert.Equal(new[] { "SYN-PLUS" }, extra.PlanTypes);
        Assert.Equal(JsonSerializer.Serialize(RuleCatalog.Baseline.Rules, RuleCatalog.Json),
            JsonSerializer.Serialize(v2.Rules.Where(r => r.Id != "BENCH_PLUS_AMOUNT").ToArray(), RuleCatalog.Json));
    }

    [Fact]
    public void DetectionMathExposesAlwaysFlipAndNeverFlipNegativeControls()
    {
        var always = new DetectionTally(); var never = new DetectionTally(); var correct = new DetectionTally();
        foreach (var truth in new[] { true, false, true, false })
        { always.Observe(truth, true); never.Observe(truth, false); correct.Observe(truth, truth); }
        Assert.Equal(2, always.FalsePositive); Assert.Equal(50, always.PrecisionPercent);
        Assert.Equal(2, never.FalseNegative); Assert.Equal(0, never.DetectionPercent);
        Assert.Equal(100, correct.DetectionPercent); Assert.Equal(0, correct.FalsePositive); Assert.Equal(0, correct.FalseNegative);
        Assert.Null(new DetectionTally().DetectionPercent); Assert.Null(new DetectionTally().PrecisionPercent);
    }

    [Fact]
    public void BulkLoadReplayAndAuditPersistEveryClaimAndBothRunsWithMatchingArtifacts()
    {
        var folder = Path.Combine(Path.GetTempPath(), "shadow-test-" + Guid.NewGuid().ToString("N"));
        var database = Path.Combine(folder, "corpus.db"); var generator = Path.Combine(folder, "results", "generator.json");
        var shadow = Path.Combine(folder, "results", "shadow.json");
        try
        {
            GeneratorProgram.Run(1000, Corpus.DefaultSeed, database, generator);
            Assert.Equal(0, ShadowProgram.Run(database, shadow, "test-run"));
            Assert.Equal(0, ShadowProgram.Run(database, Path.Combine(folder, "results", "shadow-repeat.json"), "test-run-repeat"));
            using var g = JsonDocument.Parse(File.ReadAllText(generator));
            using var s = JsonDocument.Parse(File.ReadAllText(shadow));
            var report = s.RootElement;
            Assert.Equal("completed", report.GetProperty("status").GetString());
            Assert.Equal(1000, report.GetProperty("replayedClaims").GetInt32());
            Assert.Equal(1000, report.GetProperty("persistedObservations").GetInt32());
            Assert.Equal(80, g.RootElement.GetProperty("invalidClaims").GetInt32());
            Assert.Equal(g.RootElement.GetProperty("corpusSha256").GetString(), report.GetProperty("corpusSha256").GetString());
            Assert.Equal(g.RootElement.GetProperty("expectedFlips").GetInt32(), report.GetProperty("flipCount").GetInt32());
            Assert.True(report.GetProperty("expectedFlips").GetInt32() > 0);
            Assert.Equal(100, report.GetProperty("detectionPercent").GetDouble());
            Assert.Equal(0, report.GetProperty("falsePositive").GetInt32()); Assert.Equal(0, report.GetProperty("falseNegative").GetInt32());
            Assert.Equal(1000, report.GetProperty("v1").GetProperty("oracleMatches").GetInt32());
            Assert.Equal(1000, report.GetProperty("v2").GetProperty("oracleMatches").GetInt32());
            Assert.Equal(0, report.GetProperty("byPlan").GetProperty("SYN-BASIC").GetProperty("detectedFlips").GetInt32());
            using var db = BenchmarkFiles.Open(database);
            Assert.Equal(1000, BenchmarkFiles.Count(db, "Claims")); Assert.Equal(2000, BenchmarkFiles.Count(db, "Replay"));
            Assert.Equal(3005, BenchmarkFiles.Count(db, "Audit")); // 2 rules + manifest + 1000 claims + 2000 observations + 2 summaries
            foreach (var table in new[] { "Claims", "Rules", "Metadata", "Replay", "Runs", "Audit" })
            {
                Assert.Throws<SqliteException>(() => BenchmarkFiles.Execute(db, $"DELETE FROM {table}"));
            }
            Assert.Throws<SqliteException>(() => BenchmarkFiles.Execute(db, "UPDATE Claims SET ClaimJson=ClaimJson"));
            var ledger = File.ReadAllText(Path.Combine(folder, "RESULTS.md"));
            Assert.Contains("test-run", ledger); Assert.Contains("test-run-repeat", ledger); Assert.Contains("| generator |", ledger);
            Assert.Throws<IOException>(() => GeneratorProgram.Run(1, 1, database, Path.Combine(folder, "unused.json")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void TamperedCorpusFailsWithMismatchEvidenceAndFailedLedgerRow()
    {
        var folder = Path.Combine(Path.GetTempPath(), "shadow-tamper-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dbPath = Path.Combine(folder, "corpus.db"); var output = Path.Combine(folder, "results", "shadow.json");
            GeneratorProgram.Run(100, 42, dbPath, Path.Combine(folder, "results", "generator.json"));
            // Simulate external corruption beyond normal append-only guards.
            using (var db = BenchmarkFiles.Open(dbPath))
                BenchmarkFiles.Execute(db, "DROP TRIGGER Claims_no_update; UPDATE Claims SET InvalidKind=CASE InvalidKind WHEN 'None' THEN 'MissingMember' ELSE 'None' END WHERE Sequence=1");
            Assert.Equal(1, ShadowProgram.Run(dbPath, output));
            using var doc = JsonDocument.Parse(File.ReadAllText(output));
            Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
            Assert.False(doc.RootElement.GetProperty("corpusHashMatches").GetBoolean());
            Assert.Equal(1, doc.RootElement.GetProperty("labelMismatches").GetInt32());
            Assert.NotEmpty(doc.RootElement.GetProperty("mismatches").EnumerateArray());
            Assert.Contains("| failed |", File.ReadAllText(Path.Combine(folder, "RESULTS.md")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("https://example.test/corpus.db")]
    [InlineData("Server=example;Database=claims.db")]
    [InlineData(":memory:")]
    [InlineData("claims.sqlite")]
    public void OnlyLocalDatabaseFilenamesAreAccepted(string input) => Assert.Throws<ArgumentException>(() => BenchmarkFiles.DatabasePath(input));
}
