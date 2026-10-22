using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MediBridge.Core.PriorAuth;
using MediBridge.Infrastructure;
using MediBridge.Infrastructure.PriorAuth;
using MediBridge.Parity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MediBridge.Tests;

public sealed class ManualClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
public sealed class SlaApiFactory : ClaimsApiFactory
{
    public ManualClock Clock { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => { services.RemoveAll<IClock>(); services.AddSingleton<IClock>(Clock); });
    }
}
public class PriorAuthClockTests
{
    private static readonly DateTimeOffset Receipt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    [Theory]
    [InlineData(PriorAuthPriority.Standard, 168)]
    [InlineData(PriorAuthPriority.Expedited, 72)]
    public void ExactThresholdsAndSkippedScansAreDeterministic(PriorAuthPriority priority, int hours)
    {
        var state = new PriorAuthLifecycle(priority, Receipt);
        Assert.Equal(Receipt.AddHours(hours), state.DueAt);
        Assert.Equal(Receipt.AddHours(hours * .8), state.AtRiskAt);
        Assert.Empty(PriorAuthLifecycleRules.Escalations(state, state.AtRiskAt.AddTicks(-1)));
        var risk = Assert.Single(PriorAuthLifecycleRules.Escalations(state, state.AtRiskAt));
        Assert.Equal(PriorAuthSla.AtRisk, risk.Sla);
        Assert.Empty(PriorAuthLifecycleRules.Escalations(risk, state.DueAt.AddTicks(-1)));
        Assert.Equal(PriorAuthSla.Breached, Assert.Single(PriorAuthLifecycleRules.Escalations(risk, state.DueAt)).Sla);
        var catchup = PriorAuthLifecycleRules.Escalations(state, state.DueAt.AddDays(3));
        Assert.Equal(new[] { PriorAuthSla.AtRisk, PriorAuthSla.Breached }, catchup.Select(s => s.Sla));
        Assert.Empty(PriorAuthLifecycleRules.Escalations(catchup[^1], state.DueAt.AddDays(10)));
    }
    [Fact]
    public void PendingDoesNotPauseClockAndFinalStopsEscalations()
    {
        var state = new PriorAuthLifecycle(PriorAuthPriority.Standard, Receipt);
        var pending = PriorAuthLifecycleRules.Decide(state, new("Pended"), Receipt.AddDays(1));
        Assert.Equal(state.DueAt, pending.DueAt); Assert.Null(pending.DecidedAt);
        Assert.Equal(2, PriorAuthLifecycleRules.Escalations(pending, state.DueAt).Count);
        var final = PriorAuthLifecycleRules.Decide(pending, new("Approved"), Receipt.AddDays(2));
        Assert.Empty(PriorAuthLifecycleRules.Escalations(final, Receipt.AddYears(1))); Assert.Null(final.NextCheckAt);
        Assert.Equal("PA_FINAL", PriorAuthLifecycleRules.Validate(final, new("Denied", "MissingDocumentation"), Receipt.AddDays(3))!.Code);
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("Unknown")] [InlineData("0")] [InlineData("missingdocumentation")]
    public void DenialsRequireDefinedStructuredReason(string? reason)
    {
        var state = new PriorAuthLifecycle(PriorAuthPriority.Expedited, Receipt);
        Assert.Equal("PA_DENIAL_REASON", PriorAuthLifecycleRules.Validate(state, new("Denied", reason), Receipt)!.Code);
    }
    [Fact]
    public void AllDefinedReasonsAreAcceptedAndOtherTransitionsRejectReasons()
    {
        var state = new PriorAuthLifecycle(PriorAuthPriority.Standard, Receipt);
        foreach (var reason in Enum.GetValues<DenialReason>())
            Assert.Equal(reason, PriorAuthLifecycleRules.Decide(state, new("Denied", reason.ToString()), Receipt.AddHours(1)).ReasonCode);
        Assert.Equal("PA_REASON_NOT_ALLOWED", PriorAuthLifecycleRules.Validate(state, new("Pended", "MissingDocumentation"), Receipt)!.Code);
        foreach (var decision in new[] { null, "Open", "0", "approved", "Invalid" }) Assert.NotNull(PriorAuthLifecycleRules.Validate(state, new(decision), Receipt));
        Assert.Equal("PA_CLOCK", PriorAuthLifecycleRules.Validate(state, new("Approved"), Receipt.AddTicks(-1))!.Code);
    }
    [Fact]
    public void MetricsUseFinalizedDenominatorDeduplicateSnapshotsAndCalculateEvenOddMedians()
    {
        PriorAuthLifecycle State(int hours, PriorAuthDecision decision, PriorAuthPriority priority = PriorAuthPriority.Standard) =>
            new(priority, Receipt, decision, DecidedAt: decision is PriorAuthDecision.Approved or PriorAuthDecision.Denied ? Receipt.AddHours(hours) : null);
        PriorAuthMetricObservation[] audit = [new("A", State(0, PriorAuthDecision.Open)), new("A", State(2, PriorAuthDecision.Approved)),
            new("B", State(10, PriorAuthDecision.Denied)), new("C", State(0, PriorAuthDecision.Pended)), new("D", State(0, PriorAuthDecision.Open)),
            new("E", State(3, PriorAuthDecision.Approved, PriorAuthPriority.Expedited))];
        var metrics = PriorAuthMetricMath.Compute(audit);
        Assert.Equal(5, metrics[0].Received); Assert.Equal(3, metrics[0].Finalized); Assert.Equal(2d / 3, metrics[0].ApprovalRate);
        Assert.Equal(1d / 3, metrics[0].DenialRate); Assert.Equal(5d, metrics[0].MeanTurnaroundHours); Assert.Equal(3d, metrics[0].MedianTurnaroundHours);
        Assert.Equal(6d, metrics[1].MeanTurnaroundHours); Assert.Equal(6d, metrics[1].MedianTurnaroundHours); Assert.Equal(.5, metrics[1].ApprovalRate);
        Assert.Equal(3d, metrics[2].MedianTurnaroundHours);
        foreach (var row in PriorAuthMetricMath.Compute([])) { Assert.Equal(0, row.Finalized); Assert.Null(row.ApprovalRate); Assert.Null(row.MedianTurnaroundHours); Assert.Null(row.MeanTurnaroundHours); }
    }
}

public class PriorAuthSlaApiTests
{
    private static async Task<string> Submit(HttpClient client, bool invalid = false)
    {
        var pair = PairedRequests.Generate(1)[0]; var id = "PA-SLA-" + Guid.NewGuid().ToString("N");
        var body = pair.Fhir.Replace(pair.Expected.Id, id);
        if (invalid) body = body.Replace(pair.Expected.MemberId, "INVALID");
        using var response = await client.PostAsync("/api/pa/fhir", new StringContent(body, Encoding.UTF8, "application/fhir+json"));
        Assert.Equal(invalid ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.Created, response.StatusCode);
        return id;
    }
    private static async Task Sweep(SlaApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PriorAuthLifecycleStore>();
        while (await store.SweepAsync() > 0) { }
    }
    [Fact]
    public async Task ReceiptClockEscalatesAfterRestartExactlyOnceAndDecisionReplays()
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient(); var receipt = factory.Clock.UtcNow;
        var id = await Submit(client); // sample submitted timestamp differs from receipt
        var before = await client.GetFromJsonAsync<PriorAuthResponse>("/api/pa/" + id, PriorAuthJson.Options);
        Assert.Equal(receipt, before!.Lifecycle!.ReceivedAt);
        factory.Clock.UtcNow = before.Lifecycle.DueAt.AddHours(1);
        await Task.WhenAll(Sweep(factory), Sweep(factory)); await Sweep(factory);
        var after = await client.GetFromJsonAsync<PriorAuthResponse>("/api/pa/" + id, PriorAuthJson.Options);
        Assert.Equal(PriorAuthSla.Breached, after!.Lifecycle!.Sla);
        Assert.Equal(new[] { "AtRisk", "Breached" }, after.Timeline.Where(e => e.Stage == "Escalation").Select(e => e.Outcome));
        Assert.All(after.Timeline.Where(e => e.Stage == "Escalation"), e => Assert.Equal(factory.Clock.UtcNow, e.Timestamp));
        using var keyed = factory.CreateDefaultClient(); keyed.DefaultRequestHeaders.Add("Idempotency-Key", "SYN-DECISION-REPLAY");
        var decision = new DecisionCommand("Denied", "MissingDocumentation");
        var response = await keyed.PostAsJsonAsync($"/api/pa/{id}/decisions", decision);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var replay = await keyed.PostAsJsonAsync($"/API/PA/{id}/DECISIONS", decision);
        Assert.Equal(await response.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync()); Assert.True(replay.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/pa/{id}/decisions", new DecisionCommand("Approved"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await keyed.PostAsJsonAsync($"/api/pa/{id}/decisions", new DecisionCommand("Approved"))).StatusCode);
        var report = await client.GetFromJsonAsync<JsonElement>("/api/reports/prior-auth-metrics");
        Assert.Equal(1, report.GetProperty("metrics")[0].GetProperty("denied").GetInt32());
        Assert.Equal(1d, report.GetProperty("metrics")[0].GetProperty("denialRate").GetDouble());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/pa")).GetProperty("requests").GetArrayLength());
    }
    [Fact]
    public async Task InvalidDecisionsAreSideEffectFreeAndPendingRemainsOnBoard()
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient(); var id = await Submit(client);
        var before = await client.GetStringAsync("/api/pa/" + id);
        foreach (var command in new[] { new DecisionCommand("Denied"), new("Denied", "Bogus"), new("Open"), new("Approved", "MissingDocumentation") })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync($"/api/pa/{id}/decisions", command)).StatusCode);
        Assert.Equal(before, await client.GetStringAsync("/api/pa/" + id));
        var invalid = await Submit(client, true);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/pa/{invalid}/decisions", new DecisionCommand("Approved"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/pa/UNKNOWN/decisions", new DecisionCommand("Approved"))).StatusCode);
        using var noKey = factory.CreateDefaultClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await noKey.PostAsJsonAsync($"/api/pa/{id}/decisions", new DecisionCommand("Approved"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/pa/{id}/decisions", new DecisionCommand("Pended"))).StatusCode);
        var board = await client.GetFromJsonAsync<JsonElement>("/api/pa?limit=1");
        Assert.Equal(1, board.GetProperty("counts").GetProperty("onTrack").GetInt32());
        Assert.Equal("Pended", board.GetProperty("requests")[0].GetProperty("lifecycle").GetProperty("decision").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/pa?limit=501")).StatusCode);
        var report = await client.GetFromJsonAsync<JsonElement>("/api/reports/prior-auth-metrics");
        Assert.Equal(1, report.GetProperty("metrics")[0].GetProperty("received").GetInt32());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("metrics")[0].GetProperty("approvalRate").ValueKind);
    }
    [Fact]
    public async Task DecisionsAndSweepRaceWithoutDuplicateEscalationsOrFinalEvents()
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient(); var id = await Submit(client);
        factory.Clock.UtcNow = factory.Clock.UtcNow.AddDays(10);
        await Task.WhenAll(Sweep(factory), client.PostAsJsonAsync($"/api/pa/{id}/decisions", new DecisionCommand("Approved")),
            client.PostAsJsonAsync($"/api/pa/{id}/decisions", new DecisionCommand("Denied", "SyntheticPolicyNotMet")));
        var result = await client.GetFromJsonAsync<PriorAuthResponse>("/api/pa/" + id, PriorAuthJson.Options);
        Assert.True(result!.Lifecycle!.IsFinal);
        Assert.Equal(1, result.Timeline.Count(e => e.Stage == "Decision"));
        Assert.Equal(2, result.Timeline.Count(e => e.Stage == "Escalation"));
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        // Metrics must use the append-only snapshots, not mutable projection JSON.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PriorAuthRequests SET LifecycleJson = {'{'} WHERE Id = {id}");
        var report = await client.GetAsync("/api/reports/prior-auth-metrics"); Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await report.Content.ReadAsStringAsync()).RootElement.GetProperty("metrics")[0].GetProperty("finalized").GetInt32());
    }
}
