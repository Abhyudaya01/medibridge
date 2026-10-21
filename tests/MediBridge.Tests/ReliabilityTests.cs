using System.Net;
using System.Net.Http.Json;
using MediBridge.Core;
using MediBridge.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediBridge.Tests;

public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delay) => _now += delay;
}
public sealed class TestFaults : IFaultInjector
{
    public FailureDescriptor? Failure { get; set; }
    public string? CrashStep { get; set; }
    public Task<FailureDescriptor?> BeforeAttemptAsync(string id, int attempt, CancellationToken ct) => Task.FromResult(Failure);
    public Task CheckpointAsync(string step, CancellationToken ct)
    {
        if (step == CrashStep) { CrashStep = null; throw new SyntheticCrashException(); }
        return Task.CompletedTask;
    }
}
public sealed class SyntheticCrashException : Exception;

public class FailurePolicyTests
{
    [Theory]
    [InlineData("Timeout", null, null, FailureCategory.Transient)]
    [InlineData("Http", 500, null, FailureCategory.Transient)]
    [InlineData("Http", 503, null, FailureCategory.Transient)]
    [InlineData("Sql", null, 1205, FailureCategory.Transient)]
    [InlineData("Sql", null, 40197, FailureCategory.Transient)]
    [InlineData("Sql", null, 40501, FailureCategory.Transient)]
    [InlineData("Sql", null, 40613, FailureCategory.Transient)]
    [InlineData("Sql", null, -2, FailureCategory.Transient)]
    [InlineData("Sql", null, 10928, FailureCategory.Transient)]
    [InlineData("Sql", null, 10929, FailureCategory.Transient)]
    [InlineData("Sql", null, 49918, FailureCategory.Transient)]
    [InlineData("Sql", null, 49919, FailureCategory.Transient)]
    [InlineData("Sql", null, 49920, FailureCategory.Transient)]
    [InlineData("BusinessRule", 500, null, FailureCategory.BusinessRule)]
    [InlineData("Http", 400, null, FailureCategory.Unclassified)]
    [InlineData("Sql", null, 2627, FailureCategory.Unclassified)]
    public void ClassificationIsExplicit(string kind, int? http, int? sql, FailureCategory expected) =>
        Assert.Equal(expected, FailureClassifier.Classify(new(kind, "Synthetic failure", http, sql)));

    [Fact]
    public async Task FaultInjectionIsOffByDefaultAndExplicitRateOneAlwaysInjects()
    {
        var disabled = new ConfigurableFaultInjector(new FaultOptions { TransientFailureRate = 1, AddedLatencyMs = 10000 });
        Assert.Null(await disabled.BeforeAttemptAsync("SYN-MESSAGE", 1, CancellationToken.None));
        var enabled = new ConfigurableFaultInjector(new FaultOptions { Enabled = true, TransientFailureRate = 1 });
        Assert.Equal("Timeout", (await enabled.BeforeAttemptAsync("SYN-MESSAGE", 1, CancellationToken.None))!.Kind);
    }
    [Fact]
    public void BackoffIsDeterministicExponentialBoundedAndLimitedToFiveAttempts()
    {
        Assert.Equal(new[] { 5d, 10d, 20d, 40d }, Enumerable.Range(1, 4).Select(i => RetryPolicy.Delay(i, .5)!.Value.TotalSeconds));
        Assert.Equal(2.5, RetryPolicy.Delay(1, 0)!.Value.TotalSeconds);
        Assert.Equal(7.5, RetryPolicy.Delay(1, 1)!.Value.TotalSeconds);
        Assert.Null(RetryPolicy.Delay(5, .5)); Assert.Null(RetryPolicy.Delay(0, .5));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.Delay(1, double.NaN));
    }
}

public class IdempotencyTests(ClaimsApiFactory factory) : IClassFixture<ClaimsApiFactory>
{
    private static ClaimSubmission Request() => new($"CLM-{Guid.NewGuid():N}", "0000000006", $"SYN-{Guid.NewGuid():N}", new(2026, 1, 20), ["SYN-IDEM"], 12.50m);
    [Fact]
    public async Task HeaderIsRequiredAndReplaysOriginalResponseWithLocation()
    {
        using var client = factory.CreateDefaultClient();
        var request = Request();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/claims", request)).StatusCode);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var first = await client.PostAsJsonAsync("/api/claims", request);
        var original = await first.Content.ReadAsStringAsync();
        var replay = await client.PostAsJsonAsync("/api/claims", request);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(first.StatusCode, replay.StatusCode); Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/claims", request with { Amount = 13 })).StatusCode);
    }
    [Fact]
    public async Task ConcurrentSameKeyHasOneClaimOneMessageAndOneCachedResponse()
    {
        using var client = factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var request = Request();
        var responses = await Task.WhenAll(client.PostAsJsonAsync("/api/claims", request), client.PostAsJsonAsync("/api/claims", request));
        Assert.Equal(await responses[0].Content.ReadAsStringAsync(), await responses[1].Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        Assert.Equal(1, await db.Claims.CountAsync(c => c.Id == request.Id));
        Assert.Equal(1, await db.Outbox.CountAsync(m => m.ClaimId == request.Id));
    }
    [Fact]
    public async Task UnsupportedMediaResponseReplaysWithOriginalContentType()
    {
        using var client = factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var first = await client.PostAsJsonAsync("/api/x12/837", new { text = "SYNTHETIC" });
        var replay = await client.PostAsJsonAsync("/api/x12/837", new { text = "SYNTHETIC" });
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, replay.StatusCode);
        Assert.Equal(first.Content.Headers.ContentType, replay.Content.Headers.ContentType);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task X12RejectedAndAcceptedResponsesReplayWithoutReprocessing()
    {
        using var client = factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var text = X12Samples.Load("missing-npi");
        var first = await client.PostAsync("/api/x12/837", new StringContent(text, System.Text.Encoding.UTF8, "text/plain"));
        var replay = await client.PostAsync("/api/x12/837", new StringContent(text, System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
    }
}

public class DispatcherTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("AfterLease")]
    [InlineData("AfterAttemptStarted")]
    [InlineData("AfterHandlerCommit")]
    public async Task CrashRecoveryPreservesAtLeastOnceDeliveryAndExactlyOneHandlerEffect(string? crash)
    {
        var clock = new TestClock(); var faults = new TestFaults { CrashStep = crash };
        var path = Path.Combine(Path.GetTempPath(), $"reliable-{Guid.NewGuid():N}.db");
        await using var services = Build(path, clock, faults);
        var id = "CLM-RECOVER";
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>(); await db.InitializeAsync();
            await scope.ServiceProvider.GetRequiredService<ClaimStore>().SubmitAsync(ClaimRuleTests.Valid(id), Simulation.None, clock.GetUtcNow().UtcDateTime);
        }
        var dispatcher = services.GetRequiredService<OutboxDispatcher>();
        if (crash is not null)
        {
            await Assert.ThrowsAsync<SyntheticCrashException>(() => dispatcher.DispatchOneAsync());
            clock.Advance(TimeSpan.FromSeconds(31));
        }
        await dispatcher.DispatchOneAsync();
        using var verify = services.CreateScope(); var saved = verify.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        Assert.Equal(ClaimStatus.Succeeded, (await saved.Claims.SingleAsync()).Status);
        Assert.Equal(1, await saved.Receipts.CountAsync());
        Assert.Equal(1, await saved.ClaimEvents.CountAsync(e => e.Outcome == "AttemptSucceeded"));
        Assert.Equal("Completed", (await saved.Outbox.SingleAsync()).State);
        if (crash == "AfterHandlerCommit") Assert.True(await saved.ClaimEvents.AnyAsync(e => e.Outcome == "DuplicateDeliveryIgnored"));
    }
    [Fact]
    public async Task TransientStopsAtFiveAndBusinessRuleIsNeverRetried()
    {
        foreach (var kind in new[] { "Timeout", "BusinessRule" })
        {
            var clock = new TestClock(); var faults = new TestFaults { Failure = new(kind, "Synthetic failure") };
            await using var services = Build(Path.Combine(Path.GetTempPath(), $"reliable-{Guid.NewGuid():N}.db"), clock, faults);
            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>(); await db.InitializeAsync();
                await scope.ServiceProvider.GetRequiredService<ClaimStore>().SubmitAsync(ClaimRuleTests.Valid(), Simulation.None, clock.GetUtcNow().UtcDateTime);
            }
            for (var i = 0; i < 7; i++)
            { await services.GetRequiredService<OutboxDispatcher>().DispatchOneAsync(); clock.Advance(TimeSpan.FromMinutes(2)); }
            using var check = services.CreateScope(); var saved = check.ServiceProvider.GetRequiredService<ClaimsDbContext>();
            Assert.Equal(kind == "Timeout" ? 5 : 1, (await saved.Claims.SingleAsync()).AttemptCount);
            Assert.Equal("DeadLetter", (await saved.Outbox.SingleAsync()).State);
            Assert.Equal(kind == "BusinessRule" ? 1 : 0, await saved.Corrections.CountAsync());
            if (kind == "BusinessRule") Assert.Null(saved.Entry(await saved.Claims.SingleAsync()).Property("DuplicateKey").CurrentValue);
        }
    }
    [Fact]
    public async Task TimeoutSimulationRecoversOnSecondDurableAttempt()
    {
        var clock = new TestClock(); await using var services = Build(Path.Combine(Path.GetTempPath(), $"reliable-{Guid.NewGuid():N}.db"), clock, new TestFaults());
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>(); await db.InitializeAsync();
            await scope.ServiceProvider.GetRequiredService<ClaimStore>().SubmitAsync(ClaimRuleTests.Valid(), Simulation.DownstreamTimeout, clock.GetUtcNow().UtcDateTime);
        }
        var dispatcher = services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchOneAsync(); Assert.False(await dispatcher.DispatchOneAsync());
        clock.Advance(TimeSpan.FromMinutes(1)); await dispatcher.DispatchOneAsync();
        using var check = services.CreateScope(); var claim = await check.ServiceProvider.GetRequiredService<ClaimsDbContext>().Claims.SingleAsync();
        Assert.Equal(ClaimStatus.Succeeded, claim.Status); Assert.Equal(2, claim.AttemptCount); Assert.Equal(1, claim.RetryCount);
    }
    internal static ServiceProvider Build(string path, TimeProvider clock, IFaultInjector faults)
    {
        var services = new ServiceCollection(); services.AddSingleton(clock); services.AddSingleton(faults);
        services.AddDbContext<SqliteClaimsDbContext>(o => o.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString()));
        services.AddScoped<ClaimsDbContext>(s => s.GetRequiredService<SqliteClaimsDbContext>());
        services.AddSingleton<ClaimWriteLock>(); services.AddScoped<ClaimStore>(); services.AddSingleton<OutboxDispatcher>(); return services.BuildServiceProvider();
    }
}
