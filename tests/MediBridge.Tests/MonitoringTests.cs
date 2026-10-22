using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediBridge.Core;
using MediBridge.Core.Monitoring;
using MediBridge.Core.Rules;
using MediBridge.Drift;
using MediBridge.Infrastructure;
using MediBridge.Infrastructure.Monitoring;
using MediBridge.Infrastructure.Rules;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediBridge.Tests;

public class PChartTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    [Fact]
    public void WeightedBaselineUsesCountsAndLimitsVaryWithBatchDenominator()
    {
        var chart = PCharts.Calculate([new(Start, 100, 10), new(Start.AddMinutes(5), 300, 90), new(Start.AddMinutes(10), 100, 50)], 2);
        Assert.Equal(400, chart.BaselineObservations); Assert.Equal(100, chart.BaselineFailures); Assert.Equal(.25, chart.BaselineRate);
        Assert.Equal(.1200961894323342, chart.Points[0].Lower!.Value, 12);
        Assert.Equal(.3799038105676658, chart.Points[0].Upper!.Value, 12);
        Assert.Equal(.325, chart.Points[1].Upper!.Value, 12);
        Assert.True(chart.Points[0].OutsideLimits); Assert.False(chart.Points[0].Alert); Assert.True(chart.Points[2].Alert);
    }
    [Fact]
    public void LimitsAreInclusiveAndZeroObservationBatchesDoNotCreatePoints()
    {
        var chart = PCharts.Calculate([new(Start, 36, 18), new(Start.AddMinutes(5), 36, 18), new(Start.AddMinutes(10), 0, 0),
            new(Start.AddMinutes(15), 36, 27), new(Start.AddMinutes(20), 36, 28), new(Start.AddMinutes(25), 36, 8)], 2);
        Assert.Equal(5, chart.Points.Count); Assert.False(chart.Points[2].Alert); Assert.True(chart.Points[3].Alert); Assert.True(chart.Points[4].Alert);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void DegenerateBaselinesAreExplicitAndStillDetectChanges(int failures)
    {
        var chart = PCharts.Calculate([new(Start, 100, failures), new(Start.AddMinutes(5), 100, failures), new(Start.AddMinutes(10), 100, 50)], 2);
        Assert.True(chart.Points[2].Alert); Assert.False(chart.Points[2].NormalApproximationReliable);
        Assert.Equal(failures / 100.0, chart.Points[2].Lower); Assert.Equal(chart.Points[2].Lower, chart.Points[2].Upper);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void SmallSamplesClampLimitsAndExposeApproximationWarning(int failures)
    {
        var chart = PCharts.Calculate([new(Start, 100, failures), new(Start.AddMinutes(5), 100, failures), new(Start.AddMinutes(10), 1, 0)], 2);
        var point = chart.Points[2]; Assert.InRange(point.Lower!.Value, 0, 1); Assert.InRange(point.Upper!.Value, 0, 1);
        Assert.False(point.NormalApproximationReliable);
        if (failures == 1) Assert.Equal(0, point.Lower); else Assert.Equal(1, point.Upper);
    }
    [Fact]
    public void InsufficientEmptyAndInvalidInputsNeverInventLimits()
    {
        Assert.Empty(PCharts.Calculate([]).Points);
        var chart = PCharts.Calculate([new(Start, 10, 2)]);
        Assert.Equal("InsufficientBaseline", chart.Status); Assert.Null(chart.BaselineRate); Assert.Null(chart.Points[0].Upper); Assert.False(chart.Points[0].Alert);
        Assert.Throws<ArgumentException>(() => PCharts.Calculate([new(Start, 10, 11)]));
        Assert.Throws<ArgumentException>(() => PCharts.Calculate([new(Start, -1, 0)]));
        Assert.Throws<ArgumentException>(() => PCharts.Calculate([new(Start, 1, 0), new(Start, 1, 0)]));
        Assert.Throws<ArgumentException>(() => PCharts.Calculate([new(Start.ToOffset(TimeSpan.FromHours(1)), 1, 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PCharts.Calculate([], 1));
    }
    [Fact]
    public void BoundedAccumulatorRetainsOriginalCalibrationAndRejectsDisorderedOrInvalidStreams()
    {
        var accumulator = new ChartAccumulator();
        for (var i = 0; i < 100; i++) accumulator.Add(Start.AddMinutes(i * 5).UtcTicks, 100, i < 20 ? 10 : 50);
        var chart = accumulator.Complete();
        Assert.Equal(100, accumulator.TotalBatches); Assert.Equal(80, chart.Points.Count); Assert.Equal(.1, chart.BaselineRate);
        Assert.Equal(Start.AddMinutes(40 * 5), chart.Points[20].Start); Assert.True(chart.Points[^1].Alert);
        var other = new ChartAccumulator(); other.Add(Start.AddMinutes(5).UtcTicks, 1, 0);
        Assert.Throws<ArgumentException>(() => other.Add(Start.UtcTicks, 1, 0));
        Assert.Throws<ArgumentException>(() => other.Add(Start.UtcTicks, 0, 0));
        Assert.Throws<ArgumentException>(() => new ChartAccumulator(0).Add(Start.UtcTicks, 1, 0));
        Assert.Equal(PCharts.StartTicks(Start), PCharts.StartTicks(Start.AddMinutes(4).AddSeconds(59)));
    }
    [Fact]
    public void ExecutionTraceContainsOnlyRulesThatActuallyRan()
    {
        var c = ClaimRuleTests.Valid(); c.ProviderNpi = "";
        var definition = RuleCatalog.Baseline with { Rules = RuleCatalog.Baseline.Rules.Append(new RuleClause("Claim", "PLUS_ONLY", "Amount", "Currency", "Synthetic limit.", Min: .01m, Max: 100, Scale: 2, PlanTypes: ["SYN-PLUS"])).ToArray() };
        var result = RuleEvaluator.Evaluate(definition, RuleInput.From(c));
        Assert.Contains("REQUIRED_PROVIDER_NPI", result.EvaluatedRuleIds!); Assert.DoesNotContain("NPI_CHECKSUM", result.EvaluatedRuleIds!);
        Assert.DoesNotContain("PLUS_ONLY", result.EvaluatedRuleIds!); Assert.DoesNotContain("PA_PRIORITY", result.EvaluatedRuleIds!);
        Assert.All(result.Failures, f => Assert.Contains(f.RuleId, result.EvaluatedRuleIds!));
    }
}

public class MonitoringApiTests
{
    [Fact]
    public async Task EndpointAttemptsIncludeReplaysAndBadRequestsButRulesDoNotRerunAndPartialBatchesStayOpen()
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient();
        var request = new ClaimSubmission("CLM-MONITOR", "0000000006", "SYN-MONITOR", new(2026, 1, 1), ["SYN-MONITOR"], 42);
        var key = Guid.NewGuid().ToString("N");
        async Task<HttpResponseMessage> Submit()
        { using var message = new HttpRequestMessage(HttpMethod.Post, "/api/claims") { Content = JsonContent.Create(request) }; message.Headers.Add("Idempotency-Key", key); return await client.SendAsync(message); }
        Assert.Equal(HttpStatusCode.Accepted, (await Submit()).StatusCode);
        Assert.Equal("true", (await Submit()).Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/claims", request with { Id = "CLM-MISSING-NPI", ProviderNpi = "" })).StatusCode);
        using (var bad = new StringContent("not-json", System.Text.Encoding.UTF8, "application/json"))
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/claims", bad)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/claims/SYN-NOT-FOUND")).StatusCode);
        await client.GetAsync("/health");
        var open = await client.GetFromJsonAsync<MonitoringReport>("/api/monitoring/pcharts"); Assert.Empty(open!.Series);
        factory.Clock.UtcNow = factory.Clock.UtcNow.AddMinutes(5);
        var report = await client.GetFromJsonAsync<MonitoringReport>("/api/monitoring/pcharts");
        var endpoint = Assert.Single(report!.Series, s => s.Label == "POST /api/claims");
        Assert.Equal(4, endpoint.Chart.Points[0].Observations); Assert.Equal(2, endpoint.Chart.Points[0].Failures);
        var required = Assert.Single(report.Series, s => s.Label == "Claim / REQUIRED_PROVIDER_NPI");
        Assert.Equal(2, required.Chart.Points[0].Observations); Assert.Equal(1, required.Chart.Points[0].Failures);
        var checksum = Assert.Single(report.Series, s => s.Label == "Claim / NPI_CHECKSUM");
        Assert.Equal(1, checksum.Chart.Points[0].Observations); Assert.Equal(0, checksum.Chart.Points[0].Failures);
        Assert.Equal("InsufficientBaseline", endpoint.Chart.Status);
        Assert.Contains(report.Series, s => s.Label == "GET /api/claims/{id}");
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        Assert.Equal(5, await db.EndpointObservations.CountAsync()); Assert.Equal(1, await db.EndpointObservations.CountAsync(e => e.Replayed));
        Assert.Equal(5, await db.Audit.CountAsync(e => e.EntityType == nameof(EndpointObservation)));
        var row = await db.EndpointObservations.FirstAsync(); row.StatusCode = 200;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM EndpointObservations"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE EndpointObservations SET StatusCode=200"));
    }
    [Fact]
    public async Task FixedCalibrationFlagsDriftAndSeparatesVersionsWhileLegacyRecordsAreExcluded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new SqliteClaimsDbContext(new DbContextOptionsBuilder<SqliteClaimsDbContext>().UseSqlite(connection).Options); await db.InitializeAsync();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 21; i++)
        {
            for (var j = 0; j < 10; j++) db.EndpointObservations.Add(new() { CompletedAtTicks = start.AddMinutes(5 * i).UtcTicks, Method = "POST", Route = "/api/claims", StatusCode = j < (i < 20 ? 1 : 7) ? 422 : 202 });
            foreach (var version in new[] { "v1", "v2" })
            {
                var failed = version == "v1" && i == 20;
                db.RuleEvaluations.Add(new() { TransactionId = "SYN-" + i, Domain = "Claim", LiveVersion = version, LiveHash = version,
                    ReceivedAtTicks = start.AddMinutes(i * 5).UtcTicks, LiveJson = JsonSerializer.Serialize(new RuleResult(version, failed ? [new("RULE", "Id", "Synthetic fix")] : [], EvaluatedRuleIds: ["RULE"])) });
            }
        }
        db.RuleEvaluations.Add(new() { TransactionId = "SYN-LEGACY", LiveVersion = "old", ReceivedAtTicks = start.UtcTicks, LiveJson = JsonSerializer.Serialize(new RuleResult("old", [])) });
        db.RuleEvaluations.Add(new() { TransactionId = "SYN-CORRUPT", LiveVersion = "old", ReceivedAtTicks = start.UtcTicks, LiveJson = "{" });
        await db.SaveChangesAsync();
        var result = await new MonitoringStore(db).GetAsync(start.AddMinutes(105));
        Assert.Equal(2, result.ExcludedHistoricalOrInvalidEvaluations);
        var endpoint = Assert.Single(result.Series, s => s.Kind == "Endpoint"); Assert.True(endpoint.Chart.Points[^1].Alert); Assert.Equal(.1, endpoint.Chart.BaselineRate);
        Assert.True(Assert.Single(result.Series, s => s.Version == "v1").Chart.Points[^1].Alert);
        Assert.False(Assert.Single(result.Series, s => s.Version == "v2").Chart.Points[^1].Alert);
    }
    [Fact]
    public async Task UnhandledErrorsAndClientAbortsArePersistedWithoutExposingRawPaths()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        using var services = new ServiceCollection()
            .AddDbContext<SqliteClaimsDbContext>(options => options.UseSqlite(connection))
            .AddScoped<ClaimsDbContext>(p => p.GetRequiredService<SqliteClaimsDbContext>()).BuildServiceProvider();
        using (var scope = services.CreateScope()) await scope.ServiceProvider.GetRequiredService<ClaimsDbContext>().InitializeAsync();
        var clock = new ManualClock(); var context = new DefaultHttpContext(); context.Request.Path = "/api/SYN-PRIVATE-PATH"; context.Request.Method = "POST";
        var middleware = new MonitoringMiddleware(_ => throw new InvalidOperationException("original"), NullLogger<MonitoringMiddleware>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, services.GetRequiredService<IServiceScopeFactory>(), clock));
        context.RequestAborted = new CancellationToken(true);
        var aborted = new MonitoringMiddleware(_ => throw new OperationCanceledException(), NullLogger<MonitoringMiddleware>.Instance);
        await Assert.ThrowsAsync<OperationCanceledException>(() => aborted.InvokeAsync(context, services.GetRequiredService<IServiceScopeFactory>(), clock));
        using var check = services.CreateScope(); var db = check.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        var rows = await db.EndpointObservations.ToListAsync(); Assert.Equal(2, rows.Count);
        Assert.All(rows, r => { Assert.Equal(500, r.StatusCode); Assert.True(r.Exception); Assert.Equal("/api/unmatched", r.Route); });
        Assert.Single(rows, r => r.Aborted); Assert.Equal(2, await db.Audit.CountAsync(e => e.EntityType == nameof(EndpointObservation)));
    }
    [Fact]
    public async Task TelemetryStorageFailureCannotReplaceOriginalExceptionsOrSuccessfulResponses()
    {
        var clock = new ManualClock(); var context = new DefaultHttpContext(); context.Request.Path = "/api/test"; context.Request.Method = "POST";
        using var services = new ServiceCollection().BuildServiceProvider(); // No DB registration: deliberately fail telemetry persistence.
        var middleware = new MonitoringMiddleware(_ => throw new InvalidOperationException("original"), NullLogger<MonitoringMiddleware>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, services.GetRequiredService<IServiceScopeFactory>(), clock));
        Assert.Equal("original", error.Message);
        var ok = new MonitoringMiddleware(c => { c.Response.StatusCode = 202; return Task.CompletedTask; }, NullLogger<MonitoringMiddleware>.Instance);
        await ok.InvokeAsync(context, services.GetRequiredService<IServiceScopeFactory>(), clock); Assert.Equal(202, context.Response.StatusCode);
    }
}

public class DriftBenchmarkTests
{
    [Fact]
    public void SeededStepAndStationaryPairsHaveIdenticalCalibrationAndKnownStepPosition()
    {
        var step = DriftProgram.Generate(123, true); var control = DriftProgram.Generate(123, false);
        Assert.Equal(step, DriftProgram.Generate(123, true)); Assert.NotEqual(step, DriftProgram.Generate(124, true));
        Assert.Equal(step.Take(20), control.Take(20)); Assert.Equal(60, step.Length);
        for (var i = 20; i < 60; i++) { Assert.Equal(step[i].Observations, control[i].Observations); Assert.True(step[i].Failures >= control[i].Failures); }
        Assert.Equal(DriftProgram.Measure(1, 123, true), DriftProgram.Measure(1, 123, true));
    }
    [Fact]
    public void RealDriftSimulationExportsMeasuredDelaysAndLedgerWithoutAssumingPerfectDetection()
    {
        var folder = Path.Combine(Path.GetTempPath(), "drift-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var output = Path.Combine(folder, "results", "drift.json"); DriftProgram.Run(3, 123, output);
            using var json = JsonDocument.Parse(File.ReadAllText(output)); var report = json.RootElement;
            Assert.Equal("completed", report.GetProperty("status").GetString());
            Assert.Equal(3, report.GetProperty("shiftedTrials").GetArrayLength());
            var trials = report.GetProperty("shiftedTrials").EnumerateArray().ToArray();
            Assert.Equal(trials.Count(t => t.GetProperty("batchesUntilAlert").ValueKind != JsonValueKind.Null), report.GetProperty("detectedTrials").GetInt32());
            Assert.Equal(60, report.GetProperty("exampleChart").GetProperty("points").GetArrayLength());
            Assert.Contains("| drift |", File.ReadAllText(Path.Combine(folder, "RESULTS.md")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
