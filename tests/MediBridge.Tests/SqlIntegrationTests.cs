using MediBridge.Core;
using MediBridge.Core.PriorAuth;
using MediBridge.Infrastructure.PriorAuth;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MediBridge.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace MediBridge.Tests;

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEDIBRIDGE_TEST_SQL"))) Skip = "SQL Server integration runs in the CI container."; }
}
public sealed class SqlApiFactory : ClaimsApiFactory
{
    public ManualClock Clock { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => { services.RemoveAll<IClock>(); services.AddSingleton<IClock>(Clock); });
        builder.UseSetting("ConnectionStrings:Sql", Environment.GetEnvironmentVariable("MEDIBRIDGE_TEST_SQL"));
    }
}
public class SqlIntegrationTests
{
    [SqlFact]
    public async Task SqlMigrationsIdempotencyOutboxAndConcurrencyWorkAgainstRealServer()
    {
        await using var factory = new SqlApiFactory(); using var client = factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var id = "CLM-SQL-" + Guid.NewGuid().ToString("N");
        var request = new ClaimSubmission(id, "0000000006", "SYN-SQL-" + Guid.NewGuid().ToString("N"), new(2026, 1, 20), ["SYN-SQL"], 20m);
        var response = await client.PostAsJsonAsync("/api/claims", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var replay = await client.PostAsJsonAsync("/api/claims", request);
        Assert.Equal(await response.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
            Assert.True(db.Database.IsSqlServer()); Assert.Equal(6, (await db.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal(2, await db.EndpointObservations.CountAsync(e => e.Route == "/api/claims" && e.Method == "POST"));
            Assert.True(await db.EndpointObservations.AnyAsync(e => e.Replayed));
            var monitoring = await new MediBridge.Infrastructure.Monitoring.MonitoringStore(db).GetAsync(factory.Clock.UtcNow.AddMinutes(5));
            Assert.Equal(2, Assert.Single(monitoring.Series, s => s.Label == "POST /api/claims").Chart.Points[0].Observations);
            Assert.Equal(1, Assert.Single(monitoring.Series, s => s.Label == "Claim / REQUIRED_PROVIDER_NPI").Chart.Points[0].Observations);
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE EndpointObservations SET StatusCode=500"));
        }
        var dispatcher = factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 8; i++) await Task.WhenAll(dispatcher.DispatchOneAsync(), dispatcher.DispatchOneAsync());
        using var check = factory.Services.CreateScope(); var saved = check.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        Assert.Equal(ClaimStatus.Succeeded, (await saved.Claims.SingleAsync(c => c.Id == id)).Status);
        Assert.Equal(1, await saved.Receipts.CountAsync(r => saved.Outbox.Any(m => m.Id == r.Id && m.ClaimId == id)));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => saved.Database.ExecuteSqlRawAsync("UPDATE ClaimEvents SET Detail='Forbidden'"));
        client.DefaultRequestHeaders.Remove("Idempotency-Key"); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var pair = MediBridge.Parity.PairedRequests.Generate(1)[0]; var paId = "PA-SQL-" + Guid.NewGuid().ToString("N");
        var body = pair.Fhir.Replace(pair.Expected.Id, paId);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/fhir+json");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/pa/fhir", content)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/pa/" + paId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/pa/" + paId.ToLowerInvariant())).StatusCode);
        Assert.Equal(3, await saved.PriorAuthEvents.CountAsync(e => e.RequestId == paId));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => saved.Database.ExecuteSqlRawAsync("UPDATE PriorAuthEvents SET Detail='Forbidden'"));
        factory.Clock.UtcNow = factory.Clock.UtcNow.AddDays(10);
        async Task Scan()
        {
            using var scope = factory.Services.CreateScope();
            while (await scope.ServiceProvider.GetRequiredService<PriorAuthLifecycleStore>().SweepAsync() > 0) { }
        }
        await Task.WhenAll(Scan(), Scan());
        Assert.Equal(2, await saved.PriorAuthEvents.CountAsync(e => e.RequestId == paId && e.Stage == "Escalation"));
        client.DefaultRequestHeaders.Remove("Idempotency-Key"); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync($"/api/pa/{paId}/decisions", new DecisionCommand("Denied"))).StatusCode);
        client.DefaultRequestHeaders.Remove("Idempotency-Key"); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var decision = await client.PostAsJsonAsync($"/api/pa/{paId}/decisions", new DecisionCommand("Denied", "SyntheticPolicyNotMet"));
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        var replayDecision = await client.PostAsJsonAsync($"/api/pa/{paId}/decisions", new DecisionCommand("Denied", "SyntheticPolicyNotMet"));
        Assert.Equal(await decision.Content.ReadAsStringAsync(), await replayDecision.Content.ReadAsStringAsync());
        Assert.Equal(1, await saved.PriorAuthEvents.CountAsync(e => e.RequestId == paId && e.Stage == "Decision"));
        var metrics = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/reports/prior-auth-metrics");
        Assert.Equal(1, metrics.GetProperty("metrics")[0].GetProperty("denied").GetInt32());
        Assert.Equal(240d, metrics.GetProperty("metrics")[0].GetProperty("meanTurnaroundHours").GetDouble());
        var rules = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/rules");
        Assert.Equal("phase0-v1", rules.GetProperty("activeVersion").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/rules/candidate-v2/shadow", new ShadowCommand(true, rules.GetProperty("revision").GetInt64()))).StatusCode);
        client.DefaultRequestHeaders.Remove("Idempotency-Key"); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var impact = new ClaimSubmission("CLM-SQL-IMPACT-" + Guid.NewGuid().ToString("N"), "0000000006", "SYN-SQL-IMPACT-" + Guid.NewGuid().ToString("N"), new(2026, 1, 20), ["SYN-IMPACT"], 150m, PlanType: "SYN-PLUS");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/claims", impact)).StatusCode);
        var comparison = await saved.RuleEvaluations.SingleAsync(e => e.TransactionId == impact.Id);
        Assert.Equal("Accepted", comparison.LiveOutcome); Assert.Equal("Rejected", comparison.CandidateOutcome);
        Assert.Equal(ClaimStatus.PendingProcessing, (await saved.Claims.SingleAsync(c => c.Id == impact.Id)).Status);
        var diff = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/rules/candidate-v2/shadow-diff"); Assert.Equal(1, diff.GetProperty("acceptToReject").GetInt32());
        rules = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/rules");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/rules/candidate-v2/promote", new PromoteCommand(rules.GetProperty("revision").GetInt64()))).StatusCode);
        client.DefaultRequestHeaders.Remove("Idempotency-Key"); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var promoted = impact with { Id = "CLM-SQL-NEW-" + Guid.NewGuid().ToString("N"), MemberId = "SYN-NEW-" + Guid.NewGuid().ToString("N"), Amount = 42m };
        var promotedResponse = await client.PostAsJsonAsync("/api/claims", promoted); Assert.Equal(HttpStatusCode.Accepted, promotedResponse.StatusCode);
        Assert.Equal("candidate-v2", (await saved.Claims.SingleAsync(c => c.Id == promoted.Id)).RuleVersion);
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => saved.Database.ExecuteSqlRawAsync("UPDATE RuleSets SET ContentHash='Forbidden'"));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => saved.Database.ExecuteSqlRawAsync("UPDATE RuleEvaluations SET CandidateOutcome='Accepted'"));


    }
}
