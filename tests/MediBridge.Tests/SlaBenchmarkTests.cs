using MediBridge.Core.PriorAuth;
using MediBridge.Sla;

namespace MediBridge.Tests;
public class SlaBenchmarkTests
{
    [Fact]
    public void WorkloadHasReproducibleArrivalsPrioritiesPendingAndLateDecisions()
    {
        var plans = Scenario.Generate(); Assert.Equal(10000, plans.Length);
        Assert.Equal(plans, Scenario.Generate());
        Assert.Equal(10000, plans.Select(p => p.Id).Distinct().Count());
        Assert.All(plans, p => { Assert.InRange(p.Receipt, Scenario.Start, Scenario.Start.AddDays(30).AddTicks(-1)); Assert.True(p.DecisionAt > p.Receipt); });
        Assert.Contains(plans, p => p.Priority == PriorAuthPriority.Expedited);
        Assert.Contains(plans, p => p.PendingAt != null);
        Assert.Contains(plans, p => p.DecisionAt >= Scenario.Due(p));
        Assert.Contains(plans, p => p.DecisionAt < Scenario.Risk(p));
        Assert.Contains(plans, p => p.Decision == "Denied");
        Assert.Contains(plans, p => p.DecisionAt > Scenario.Start.AddDays(30));
        Assert.NotEqual(plans, Scenario.Generate(seed: 1));
    }
    [Fact]
    public async Task RealSqliteSimulationWritesObservationsAndSummaryWithIndependentOracleMatches()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sla-test-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(folder, "results", "sla.json");
        try
        {
            Assert.Equal(0, await SlaProgram.RunAsync(20, 278837, output));
            using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var root = report.RootElement;
            Assert.Equal("completed", root.GetProperty("status").GetString());
            Assert.Equal(20, root.GetProperty("requestCount").GetInt32());
            Assert.Equal(20, root.GetProperty("independentOracleMatches").GetInt32());
            var rows = root.GetProperty("requests").EnumerateArray().ToArray();
            Assert.All(rows, row => Assert.True(row.GetProperty("passed").GetBoolean()));
            Assert.Equal(rows.Count(r => r.GetProperty("missedDeadline").GetBoolean()), root.GetProperty("missedDeadlines").GetInt32());
            Assert.Equal(rows.Sum(r => r.GetProperty("atRiskEscalations").GetInt32() + r.GetProperty("breachEscalations").GetInt32()), root.GetProperty("escalationsFired").GetInt32());
            Assert.Contains(root.GetProperty("runId").GetString()!, await File.ReadAllTextAsync(Path.Combine(folder, "RESULTS.md")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public void SimulationPollRoundingAndIndependentDeadlineOracleMatchDocumentedBoundaries()
    {
        var instant = Scenario.Start.AddSeconds(1);
        Assert.Equal(Scenario.Start.AddMinutes(1), Scenario.Tick(instant)); Assert.Equal(Scenario.Start, Scenario.Tick(Scenario.Start));
        var plan = new PlannedRequest("PA-TEST", PriorAuthPriority.Standard, instant, null, instant.AddDays(8), "Approved", null);
        Assert.Equal(instant.AddHours(168), Scenario.Due(plan)); Assert.Equal(instant.AddHours(134.4), Scenario.Risk(plan));
        plan = plan with { Priority = PriorAuthPriority.Expedited };
        Assert.Equal(instant.AddHours(72), Scenario.Due(plan)); Assert.Equal(instant.AddHours(57.6), Scenario.Risk(plan));
    }
}
