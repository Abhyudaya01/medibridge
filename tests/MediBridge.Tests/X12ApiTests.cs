using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MediBridge.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediBridge.Tests;

public class X12ApiTests(ClaimsApiFactory factory) : IClassFixture<ClaimsApiFactory>
{
    private static string Unique(string text, string suffix) => text.Replace("CLM-X12-", $"CLM-X12-{suffix}-").Replace("SYN-X12-", $"SYN-X12-{suffix}-");
    private static StringContent Content(string text) => new(text, Encoding.UTF8, "text/plain");
    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task ImportMapsClaimsPersistsAuditAndReturnsAccepted999()
    {
        using var client = factory.CreateClient();
        var suffix = Suffix();
        var response = await client.PostAsync("/api/x12/837", Content(Unique(X12Samples.Load(), suffix)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var result = Assert.Single(body.GetProperty("interchanges").EnumerateArray());
        Assert.Equal("Accepted", result.GetProperty("acknowledgment").GetProperty("status").GetString());
        Assert.Equal("000000101", result.GetProperty("interchangeControlNumber").GetString());
        var claim = Assert.Single(result.GetProperty("parsedClaims").EnumerateArray()).GetProperty("claim");
        Assert.Equal($"CLM-X12-{suffix}-VALID", claim.GetProperty("id").GetString());
        var saved = await client.GetFromJsonAsync<JsonElement>($"/api/claims/CLM-X12-{suffix}-VALID");
        var first = saved.GetProperty("timeline")[0];
        Assert.Equal("X12Intake", first.GetProperty("stage").GetString());
        Assert.Contains("SYN001", first.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("missing-npi")]
    [InlineData("bad-delimiters")]
    public async Task MalformedInterchangeReturns422WithLocationsAndNoWrites(string sample)
    {
        using var client = factory.CreateClient();
        var before = await client.GetFromJsonAsync<JsonElement>("/api/claims");
        var response = await client.PostAsync("/api/x12/837", Content(Unique(X12Samples.Load(sample), Suffix())));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ack = body.GetProperty("interchanges")[0].GetProperty("acknowledgment");
        Assert.Equal("Rejected", ack.GetProperty("status").GetString());
        Assert.True(ack.GetProperty("errors").GetArrayLength() > 0);
        Assert.True(ack.GetProperty("errors")[0].GetProperty("segmentPosition").GetInt32() > 0);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/claims");
        Assert.Equal(before.GetArrayLength(), after.GetArrayLength());
    }

    [Fact]
    public async Task ValidFirstClaimInRejectedMultiClaimInterchangeIsNotImported()
    {
        using var client = factory.CreateClient();
        var suffix = Suffix();
        var text = Unique(X12Samples.Load("multi-claim"), suffix).Replace("SYNPROC2", "");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/api/x12/837", Content(text))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/claims/CLM-X12-{suffix}-MULTI-A")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/claims/CLM-X12-{suffix}-MULTI-B")).StatusCode);
    }

    [Fact]
    public async Task DuplicateClaimsAreSuppressedButSyntaxAcknowledgmentStaysAccepted()
    {
        using var client = factory.CreateClient();
        var text = Unique(X12Samples.Load("duplicate-claim"), Suffix());
        var response = await client.PostAsync("/api/x12/837", Content(text));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var result = body.GetProperty("interchanges")[0];
        Assert.Equal("Accepted", result.GetProperty("acknowledgment").GetProperty("status").GetString());
        Assert.Equal(new[] { "PendingProcessing", "Suppressed" }, result.GetProperty("outcomes").EnumerateArray().Select(c => c.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task ReplayLeavesExistingClaimAndTimelineUnchanged()
    {
        using var client = factory.CreateClient();
        var suffix = Suffix();
        var text = Unique(X12Samples.Load(), suffix);
        await client.PostAsync("/api/x12/837", Content(text));
        var before = await client.GetStringAsync($"/api/claims/CLM-X12-{suffix}-VALID");
        var response = await client.PostAsync("/api/x12/837", Content(text));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DuplicateId", body.GetProperty("interchanges")[0].GetProperty("outcomes")[0].GetProperty("status").GetString());
        Assert.Equal(before, await client.GetStringAsync($"/api/claims/CLM-X12-{suffix}-VALID"));
    }

    [Fact]
    public async Task EachInterchangeIsIndependentIncludingDifferentDelimiters()
    {
        using var client = factory.CreateClient();
        var suffix = Suffix();
        var first = Unique(X12Samples.Load("missing-npi"), suffix);
        var second = Unique(X12Samples.Load(), suffix).Replace('*', '|').Replace(':', '>').Replace('^', '!').Replace('~', '$');
        var response = await client.PostAsync("/api/x12/837", Content(first + second));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "Rejected", "Accepted" }, body.GetProperty("interchanges").EnumerateArray().Select(i => i.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/claims/CLM-X12-{suffix}-VALID")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/claims/CLM-X12-{suffix}-NO-NPI")).StatusCode);
    }

    [Fact]
    public async Task DatabaseFailureRollsBackEntireInterchange()
    {
        using var client = factory.CreateClient();
        const string suffix = "ATOMIC";
        var failedId = $"CLM-X12-{suffix}-MULTI-B";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER TestFailX12 BEFORE INSERT ON Claims WHEN NEW.Id = 'CLM-X12-ATOMIC-MULTI-B' BEGIN SELECT RAISE(ABORT, 'Synthetic test failure'); END;");
        var response = await client.PostAsync("/api/x12/837", Content(Unique(X12Samples.Load("multi-claim"), suffix)));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(await db.Claims.AnyAsync(c => c.Id == $"CLM-X12-{suffix}-MULTI-A" || c.Id == failedId));
        Assert.False(await db.ClaimEvents.AnyAsync(e => e.ClaimId == $"CLM-X12-{suffix}-MULTI-A" || e.ClaimId == failedId));
    }

    [Fact]
    public async Task ContentTypeAndEmptyInputHaveExplicitResponses()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsJsonAsync("/api/x12/837", new { input = "ISA" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/api/x12/837", Content(""))).StatusCode);
        using var latin = new StringContent("ISA", Encoding.Latin1, "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/x12/837", latin)).StatusCode);
        var swagger = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        Assert.True(swagger.GetProperty("paths").GetProperty("/api/x12/837").GetProperty("post").GetProperty("requestBody").GetProperty("content").TryGetProperty("text/plain", out _));
    }

    [Fact]
    public async Task OversizedAndInvalidUtf8BodiesAreRejectedBeforeImport()
    {
        using var client = factory.CreateClient();
        var before = await client.GetFromJsonAsync<JsonElement>("/api/claims");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/x12/837", Content(new string('X', 65_537)))).StatusCode);
        using var invalid = new ByteArrayContent([0x49, 0x53, 0x41, 0xFF]);
        invalid.Headers.ContentType = new("text/plain");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/x12/837", invalid)).StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/claims");
        Assert.Equal(before.GetArrayLength(), after.GetArrayLength());
    }
}
