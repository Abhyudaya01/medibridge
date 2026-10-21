using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediBridge.Core.PriorAuth;
using MediBridge.Infrastructure;
using MediBridge.Infrastructure.PriorAuth;
using MediBridge.Parity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediBridge.Tests;

public class PriorAuthMapperTests
{
    [Fact]
    public void TwoHundredPairsMatchIndependentCanonicalAndOutcomeOracles()
    {
        var cases = PairedRequests.Generate();
        Assert.Equal(160, cases.Count(p => p.ExpectedOutcome == "Accepted"));
        Assert.Equal(40, cases.Count(p => p.ExpectedOutcome == "Rejected"));
        foreach (var pair in cases)
        {
            var x = X12278Mapper.Map(pair.X12); var f = FhirPriorAuthMapper.Map(pair.Fhir);
            Assert.True(PriorAuthRules.Equivalent(pair.Expected, x.Request), $"X12 case {pair.Number}: {JsonSerializer.Serialize(x.Errors)}");
            Assert.True(PriorAuthRules.Equivalent(pair.Expected, f.Request), $"FHIR case {pair.Number}: {JsonSerializer.Serialize(f.Errors)}");
            Assert.Equal(pair.ExpectedOutcome, x.Outcome); Assert.Equal(pair.ExpectedOutcome, f.Outcome);
            Assert.Equal(pair.ExpectedRules, x.Errors.Select(e => e.RuleId)); Assert.Equal(pair.ExpectedRules, f.Errors.Select(e => e.RuleId));
            Assert.All(x.Errors, e => { Assert.NotNull(e.SegmentId); Assert.True(e.SegmentPosition > 0); Assert.True(e.ElementPosition > 0); });
            Assert.All(f.Errors, e => Assert.StartsWith("Claim.", e.Path));
        }
    }

    [Fact]
    public void EqualityDetectsEachCanonicalFieldAndNormalizationIsIdempotent()
    {
        var expected = PairedRequests.Generate(1)[0].Expected;
        foreach (var changed in new[] { expected with { Id = "PA-DIFFERENT" }, expected with { MemberId = "SYN-DIFFERENT" }, expected with { RequestingProviderNpi = "0000000014" },
            expected with { ServiceCodes = ["SYN-DIFFERENT"] }, expected with { Priority = PriorAuthPriority.Standard }, expected with { SubmittedAt = expected.SubmittedAt!.Value.AddSeconds(1) } })
            Assert.False(PriorAuthRules.Equivalent(expected, changed));
        Assert.False(PriorAuthRules.Equivalent(expected, null));
        Assert.True(PriorAuthRules.Equivalent(expected, PriorAuthRules.Normalize(PriorAuthRules.Normalize(expected))));
    }

    [Theory]
    [InlineData("", "PA_X12_SYNTAX")]
    [InlineData("ISA*broken~", "PA_X12_SYNTAX")]
    public void BrokenX12ReturnsLocatedErrors(string input, string rule)
    {
        var result = X12278Mapper.Map(input);
        Assert.Equal("Rejected", result.Outcome); Assert.Contains(result.Errors, e => e.RuleId == rule && e.SegmentId == "ISA" && e.SegmentPosition == 1);
    }

    [Fact]
    public void X12ChecksDelimitersCountsControlsHierarchyTestFlagAndCardinality()
    {
        var text = PairedRequests.Generate(2)[1].X12;
        foreach (var bad in new[] { text.Replace("*T*:", "*P*:"), text.Replace("*0001~GE", "*9999~GE"), text.Replace("HL*2*1*21", "HL*2*9*21"),
            text.Replace("SE*13*0001", "SE*999*0001"), text.Replace("*005010X217", "*WRONG"), text.Replace('*', ':'), text + text,
            text.Replace("SV1*HC:", "SV1*ZZ:"), text.Replace("UM*HS*I*30", "DTP*HS*I*30") })
        {
            var result = X12278Mapper.Map(bad);
            Assert.Equal("Rejected", result.Outcome); Assert.NotEmpty(result.Errors);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"resourceType\":\"Patient\"}")]
    [InlineData("{\"resourceType\":\"Claim\",\"resourceType\":\"Patient\"}")]
    public void InvalidFhirAlwaysReturnsErrorsRatherThanThrowing(string text)
    {
        var result = FhirPriorAuthMapper.Map(text);
        Assert.Equal("Rejected", result.Outcome); Assert.NotEmpty(result.Errors); Assert.All(result.Errors, e => Assert.NotNull(e.Path));
    }

    [Theory]
    [InlineData("use")]
    [InlineData("created")]
    [InlineData("patient")]
    [InlineData("provider")]
    [InlineData("insurance")]
    [InlineData("item")]
    [InlineData("priority")]
    public void MissingFhirFieldsAreRejected(string field)
    {
        var json = JsonNode.Parse(PairedRequests.Generate(1)[0].Fhir)!.AsObject(); json.Remove(field);
        Assert.Equal("Rejected", FhirPriorAuthMapper.Map(json.ToJsonString()).Outcome);
    }

    [Fact]
    public void FhirRejectsAmbiguousReferencesExtensionsWrongUseAndUnsupportedElements()
    {
        var pair = PairedRequests.Generate(1)[0];
        var variants = new List<JsonNode>();
        var external = JsonNode.Parse(pair.Fhir)!; external["patient"]!["reference"] = "https://synthetic.invalid/Patient/SYN-1"; variants.Add(external);
        var conflicting = JsonNode.Parse(pair.Fhir)!; conflicting["patient"]!["identifier"] = new JsonObject { ["system"] = FhirPriorAuthMapper.MemberSystem, ["value"] = "SYN-DIFFERENT" }; variants.Add(conflicting);
        var use = JsonNode.Parse(pair.Fhir)!; use["use"] = "claim"; variants.Add(use);
        var unknown = JsonNode.Parse(pair.Fhir)!; unknown["extension"]![0]!["url"] = "urn:synthetic:unknown"; variants.Add(unknown);
        var multiple = JsonNode.Parse(pair.Fhir)!; multiple["extension"]![0]!["valueCodeableConcept"]!["coding"]!.AsArray().Add(new JsonObject { ["system"] = FhirPriorAuthMapper.LevelSystem, ["code"] = "E" }); variants.Add(multiple);
        var modifier = JsonNode.Parse(pair.Fhir)!; modifier["modifierExtension"] = new JsonArray(); variants.Add(modifier);
        var unsupported = JsonNode.Parse(pair.Fhir)!; unsupported["careTeam"] = new JsonArray(); variants.Add(unsupported);
        var wrongContained = JsonNode.Parse(pair.Fhir)!; wrongContained["contained"]!.AsArray().Add(new JsonObject { ["resourceType"] = "Binary", ["id"] = "other", ["contentType"] = "text/plain" }); variants.Add(wrongContained);
        foreach (var variant in variants) Assert.Equal("Rejected", FhirPriorAuthMapper.Map(variant.ToJsonString()).Outcome);
    }

    [Fact]
    public void SubmittedTimeRequiresSecondsAndOffsetAndNormalizesEquivalentInstants()
    {
        var pair = PairedRequests.Generate(1)[0];
        foreach (var value in new[] { "2026-01-20", "2026-01-20T12:00:00", "invalid", "2026-01-20T12:00:00.123Z" })
        {
            var node = JsonNode.Parse(pair.Fhir)!; node["created"] = value;
            Assert.Equal("Rejected", FhirPriorAuthMapper.Map(node.ToJsonString()).Outcome);
        }
        Assert.Equal(TimeSpan.Zero, FhirPriorAuthMapper.Map(pair.Fhir).Request!.SubmittedAt!.Value.Offset);
    }

    [Fact]
    public async Task ParityHarnessWritesActualSummaryAndLedger()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.GetFullPath($"../../../../../benchmarks/parity/bin/{configuration}/net10.0/MediBridge.Parity.dll", AppContext.BaseDirectory);
        var folder = Path.Combine(Path.GetTempPath(), "pa-parity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var output = Path.Combine(folder, "results/parity.json");
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { dll, "--output", output }) info.ArgumentList.Add(arg);
            using var child = Process.Start(info)!; var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(child.ExitCode == 0, await stdout + await stderr);
            var json = JsonDocument.Parse(await File.ReadAllTextAsync(output)).RootElement;
            Assert.Equal(200, json.GetProperty("totalPairs").GetInt32()); Assert.Equal(200, json.GetProperty("expectedMatches").GetInt32());
            Assert.Equal(100, json.GetProperty("parityPercent").GetDouble()); Assert.Equal(200, json.GetProperty("cases").GetArrayLength());
            Assert.Contains("200/200", await File.ReadAllTextAsync(Path.Combine(folder, "RESULTS.md")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

public class PriorAuthApiTests(ClaimsApiFactory factory) : IClassFixture<ClaimsApiFactory>
{
    private static string Fhir(string id, bool invalid = false)
    {
        var pair = PairedRequests.Generate(1)[0];
        return pair.Fhir.Replace(pair.Expected.Id, id).Replace(pair.Expected.MemberId, invalid ? "NOT-SYNTHETIC" : "SYN-API-" + id);
    }
    private static StringContent Content(string value, string type = "application/fhir+json") => new(value, Encoding.UTF8, type);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothFormatsPersistCanonicalAndTimelineWithIdempotentReplay(bool x12)
    {
        using var client = factory.CreateDefaultClient(); var key = Guid.NewGuid().ToString("N"); client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var id = "PA-API-" + Guid.NewGuid().ToString("N");
        var pair = PairedRequests.Generate(1)[0]; var body = (x12 ? pair.X12 : pair.Fhir).Replace(pair.Expected.Id, id);
        var endpoint = x12 ? "/api/pa/x12-278" : "/api/pa/fhir";
        var response = await client.PostAsync(endpoint, Content(body, x12 ? "text/plain" : "application/fhir+json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.Equal("/api/pa/" + id, response.Headers.Location?.ToString());
        var replay = await client.PostAsync(endpoint, Content(body, x12 ? "text/plain" : "application/fhir+json"));
        Assert.Equal(await response.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync()); Assert.True(replay.Headers.Contains("Idempotency-Replayed"));
        var detail = JsonDocument.Parse(await client.GetStringAsync("/api/pa/" + id)).RootElement;
        Assert.Equal("Accepted", detail.GetProperty("outcome").GetString()); Assert.Equal(3, detail.GetProperty("timeline").GetArrayLength());
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        Assert.Equal(1, await db.PriorAuthRequests.CountAsync(r => r.Id == id)); Assert.Equal(3, await db.PriorAuthEvents.CountAsync(e => e.RequestId == id));
        Assert.True(await db.Audit.AnyAsync(a => a.EntityType == nameof(PriorAuthRecord) && a.EntityId == id));
    }

    [Fact]
    public async Task BusinessRejectionIsPersistedAndDuplicateIdOrChangedKeyPayloadDoesNotAlterTimeline()
    {
        using var client = factory.CreateClient(); var id = "PA-BAD-" + Guid.NewGuid().ToString("N");
        var rejection = await client.PostAsync("/api/pa/fhir", Content(Fhir(id, true)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejection.StatusCode);
        var before = await client.GetStringAsync("/api/pa/" + id);
        Assert.Contains("PA_MEMBER", before);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/pa/fhir", Content(Fhir(id)))).StatusCode);
        Assert.Equal(before, await client.GetStringAsync("/api/pa/" + id));
        using var keyed = factory.CreateDefaultClient(); keyed.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        await keyed.PostAsync("/api/pa/fhir", Content(Fhir("PA-KEY-" + Guid.NewGuid().ToString("N"))));
        Assert.Equal(HttpStatusCode.Conflict, (await keyed.PostAsync("/api/pa/fhir", Content(Fhir("PA-KEY-DIFFERENT")))).StatusCode);
    }

    [Fact]
    public async Task TransportFailuresAreBoundedAndDoNotCreatePaRecords()
    {
        using var client = factory.CreateDefaultClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/pa/fhir", Content("{}"))).StatusCode);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/pa/fhir", Content("{}", "text/plain"))).StatusCode);
        client.DefaultRequestHeaders.Remove("Idempotency-Key"); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/pa/fhir", Content("{"))).StatusCode);
        client.DefaultRequestHeaders.Remove("Idempotency-Key"); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/pa/fhir", Content(new string('x', 65537)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/pa/PA-NOT-FOUND")).StatusCode);
    }

    [Fact]
    public async Task DemoSamplesAreServedAndMapToTheSameCanonicalRequest()
    {
        using var client = factory.CreateClient();
        var x = X12278Mapper.Map(await client.GetStringAsync("/samples/pa-x12-278.txt"));
        var f = FhirPriorAuthMapper.Map(await client.GetStringAsync("/samples/pa-fhir.json"));
        Assert.Equal("Accepted", x.Outcome); Assert.Equal("Accepted", f.Outcome);
        Assert.True(PriorAuthRules.Equivalent(x.Request, f.Request));
        Assert.Contains("Submit a prior authorization request", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task PaTimelineRejectsTrackedAndDirectSqlMutation()
    {
        using var client = factory.CreateClient(); var id = "PA-AUDIT-" + Guid.NewGuid().ToString("N");
        await client.PostAsync("/api/pa/fhir", Content(Fhir(id)));
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        var audit = await db.PriorAuthEvents.FirstAsync(e => e.RequestId == id); audit.Detail = "Forbidden";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE PriorAuthEvents SET Detail='Forbidden'"));
        db.PriorAuthRequests.Add(new PriorAuthRecord { Id = "PA-UNTRACKED" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
