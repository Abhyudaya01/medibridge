using System.Diagnostics;
using System.Text.Json;
using MediBridge.Chaos;

namespace MediBridge.Tests;

public class BenchmarkTests
{
    private static Observation Row(int affected, int recovered) => new(1, "AfterLease", 1, 137,
        50, 5, 0, 0, 50, 50, affected + 2, affected, recovered, 0, 0, 45, 0, 0, 45, 1.5);

    [Fact]
    public void RecoveryPercentUsesAffectedClaimsRatherThanFailureEventsOrIterationAverages()
    {
        var totals = ChaosTotals.From([Row(1, 1), Row(9, 7)]);
        Assert.Equal(10, totals.TransientAffectedClaims);
        Assert.Equal(8, totals.TransientRecoveredClaims);
        Assert.Equal(80.0, totals.TransientRecoveryPercent);
        Assert.Equal(14, totals.TransientFailureEvents);
        Assert.Equal(100, totals.Claims);
    }

    [Fact]
    public void BusinessRuleMetricRetainsOriginalRejectionEvenIfLaterProcessingClearsCategory()
    {
        var claim = new MediBridge.Core.Claim { Id = "CLM-SYN-BAD", AttemptCount = 2,
            Status = MediBridge.Core.ClaimStatus.Succeeded, FailureCategory = MediBridge.Core.FailureCategory.None };
        var events = new[] { new MediBridge.Core.ClaimEvent { ClaimId = claim.Id, Stage = "Validation", Outcome = "Rejected" } };
        var result = ChaosMeasurements.BusinessRules([claim], events);
        Assert.Equal(1, result.Rejected); Assert.Equal(1, result.Retried); Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public void NoTransientFailuresHasUndefinedRecoveryPercent()
    {
        Assert.Null(ChaosTotals.From([Row(0, 0)]).TransientRecoveryPercent);
        Assert.Null(ChaosTotals.From([]).TransientRecoveryPercent);
    }

    [Fact]
    public void FailureDrawsRepeatForClaimAttemptAndExerciseBothOutcomes()
    {
        var draws = Enumerable.Range(0, 100).Select(i => ChaosFaults.Inject(1701, $"CLM-SYN-{i}", 1)).ToArray();
        Assert.Equal(draws, Enumerable.Range(0, 100).Select(i => ChaosFaults.Inject(1701, $"CLM-SYN-{i}", 1)));
        Assert.Contains(true, draws); Assert.Contains(false, draws);
    }

    [Fact]
    public async Task RealChaosProcessesWriteMeasuredJsonAndLedgerAndDrainAfterCrashes()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.GetFullPath($"../../../../../benchmarks/chaos/bin/{configuration}/net10.0/MediBridge.Chaos.dll", AppContext.BaseDirectory);
        var folder = Path.Combine(Path.GetTempPath(), "chaos-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var json = Path.Combine(folder, "chaos.json"); var ledger = Path.Combine(folder, "RESULTS.md");
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { dll, "--iterations", "3", "--seed", "1701", "--output", json, "--results", ledger }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
            finally { if (!process.HasExited) process.Kill(true); }
            Assert.True(process.ExitCode == 0, await output + await error);
            var report = JsonSerializer.Deserialize<ChaosReport>(await File.ReadAllTextAsync(json), ChaosProgram.Json)!;
            Assert.Equal("completed", report.Status); Assert.Equal(3, report.RequestedIterations);
            Assert.Equal(150, report.Totals.Claims); Assert.Equal(15, report.Totals.InvalidClaims);
            Assert.Equal(150, report.Totals.ReplaySuppressed); Assert.Equal(0, report.Totals.PendingOutbox);
            Assert.Equal(0, report.Totals.DuplicateClaims); Assert.Equal(0, report.Totals.DuplicateHandlerEffects);
            Assert.Equal(0, report.Totals.BusinessRuleFailuresRetried);
            Assert.Equal(3, report.Observations.Count); Assert.All(report.Observations, row => Assert.NotEqual(0, row.CrashExitCode));
            Assert.Contains(report.RunId, await File.ReadAllTextAsync(ledger));
            Assert.Contains("150 claims", await File.ReadAllTextAsync(ledger));
        }
        finally { Directory.Delete(folder, true); }
    }
}
