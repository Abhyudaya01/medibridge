using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediBridge.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediBridge.Tests;

public sealed class ClaimsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"medibridge-tests-{Guid.NewGuid():N}.db");
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Database:Path", _path);
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" }) File.Delete(path);
    }
}

public class ClaimApiTests(ClaimsApiFactory factory) : IClassFixture<ClaimsApiFactory>
{
    private static ClaimSubmission Request(string? id = null) => new(id ?? $"CLM-{Guid.NewGuid():N}", "0000000006", $"SYN-{Guid.NewGuid():N}", new(2026, 1, 20), ["SYN-TEST"], 42.50m);

    [Fact]
    public async Task ListAndSeedTimelinesShowAllScenarios()
    {
        using var client = factory.CreateClient();
        var claims = await client.GetFromJsonAsync<JsonElement>("/api/claims");
        foreach (var (id, status, category, outcomes) in new[]
        {
            ("CLM-1001", "Rejected", "BusinessRule", new[] { "Received", "Rejected" }),
            ("CLM-1002", "Succeeded", "None", new[] { "Received", "Passed", "Passed", "Passed", "Timeout", "RetryPending", "Retrying", "Succeeded" }),
            ("CLM-1003", "Suppressed", "Duplicate", new[] { "Received", "Passed", "Suppressed" }),
            ("CLM-1004", "ManualReview", "RuleVersionConflict", new[] { "Received", "Passed", "Passed", "ManualReview" })
        })
        {
            Assert.Contains(claims.EnumerateArray(), c => c.GetProperty("id").GetString() == id);
            var detail = await client.GetFromJsonAsync<JsonElement>($"/api/claims/{id}");
            Assert.Equal(status, detail.GetProperty("claim").GetProperty("status").GetString());
            Assert.Equal(category, detail.GetProperty("claim").GetProperty("failureCategory").GetString());
            Assert.Equal(outcomes, detail.GetProperty("timeline").EnumerateArray().Select(e => e.GetProperty("outcome").GetString()));
        }
    }

    [Fact]
    public async Task MissingClaimReturns404()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/claims/CLM-UNKNOWN")).StatusCode);
    }

    [Fact]
    public async Task ValidSubmissionPersistsAndReturnsLocationAndTimeline()
    {
        using var client = factory.CreateClient();
        var request = Request();
        var response = await client.PostAsJsonAsync("/api/claims", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/claims/{request.Id}", response.Headers.Location?.ToString());
        var result = await client.GetFromJsonAsync<JsonElement>(response.Headers.Location);
        Assert.Equal("Succeeded", result.GetProperty("claim").GetProperty("status").GetString());
        Assert.True(result.GetProperty("timeline").GetArrayLength() >= 5);
    }

    [Fact]
    public async Task ValidationRejectionIsAuditedWithActionableFailure()
    {
        using var client = factory.CreateClient();
        var request = Request() with { ProviderNpi = "" };
        var response = await client.PostAsJsonAsync("/api/claims", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        var failure = Assert.Single(detail.GetProperty("validation").EnumerateArray());
        Assert.Equal("REQUIRED_PROVIDER_NPI", failure.GetProperty("ruleId").GetString());
        Assert.Equal("ProviderNpi", failure.GetProperty("field").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("suggestedFix").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/claims/{request.Id}")).StatusCode);
        Assert.Equal(2, detail.GetProperty("timeline").GetArrayLength());
    }

    [Fact]
    public async Task DuplicateIsSuppressedAndIdReplayDoesNotAppendEvents()
    {
        using var client = factory.CreateClient();
        var request = Request();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/claims", request)).StatusCode);
        var duplicate = await client.PostAsJsonAsync("/api/claims", request with { Id = $"CLM-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var body = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Suppressed", body.GetProperty("claim").GetProperty("status").GetString());
        Assert.Equal("DUPLICATE_CLAIM", body.GetProperty("validation")[0].GetProperty("ruleId").GetString());
        var before = await client.GetStringAsync($"/api/claims/{request.Id}");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/claims", request)).StatusCode);
        Assert.Equal(before, await client.GetStringAsync($"/api/claims/{request.Id}"));
    }

    [Fact]
    public async Task ConcurrentMatchingSubmissionsHaveOneWinner()
    {
        using var client = factory.CreateClient();
        var request = Request();
        var results = await Task.WhenAll(client.PostAsJsonAsync("/api/claims", request), client.PostAsJsonAsync("/api/claims", request with { Id = $"CLM-{Guid.NewGuid():N}" }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task TimeoutAndRuleVersionScenariosAreAvailableThroughApi()
    {
        using var client = factory.CreateClient();
        var retry = await client.PostAsJsonAsync("/api/claims", Request() with { Simulation = MediBridge.Core.Simulation.DownstreamTimeout });
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("timeline").EnumerateArray(), e => e.GetProperty("outcome").GetString() == "RetryPending");
        var review = await client.PostAsJsonAsync("/api/claims", Request() with { RuleVersion = "phase0-v0" });
        Assert.Equal(HttpStatusCode.Accepted, review.StatusCode);
        body = await review.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RULE_VERSION_CONFLICT", body.GetProperty("validation")[0].GetProperty("ruleId").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"serviceDate\":\"not-a-date\"}")]
    [InlineData("not json")]
    public async Task InvalidRequestsReturn400(string payload)
    {
        using var client = factory.CreateClient();
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/claims", content)).StatusCode);
    }

    [Fact]
    public async Task SwaggerAndDemoAreServed()
    {
        using var client = factory.CreateClient();
        Assert.Contains("Synthetic data. Not affiliated with any payer.", await client.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html")).StatusCode);
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/claims", out _));
    }

    [Fact]
    public async Task SeedIsIdempotentAcrossStartupRuns()
    {
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        var count = await db.ClaimEvents.CountAsync(e => e.ClaimId == "CLM-1002");
        await SyntheticSeed.RunAsync(scope.ServiceProvider.GetRequiredService<ClaimStore>());
        await SyntheticSeed.RunAsync(scope.ServiceProvider.GetRequiredService<ClaimStore>());
        Assert.Equal(count, await db.ClaimEvents.CountAsync(e => e.ClaimId == "CLM-1002"));
        Assert.Equal(4, await db.Claims.CountAsync(c => c.Id == "CLM-1001" || c.Id == "CLM-1002" || c.Id == "CLM-1003" || c.Id == "CLM-1004"));
    }
}
