using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MediBridge.Core;
using MediBridge.Core.Rules;
using MediBridge.Infrastructure;
using MediBridge.Infrastructure.Rules;
using MediBridge.Parity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediBridge.Tests;

public class RuleEvaluatorTests
{
    [Fact]
    public void JsonBaselinePreservesExactCurrencyBoundariesAndDoesNotMutateInput()
    {
        var claim = ClaimRuleTests.Valid(); claim.Amount = 9999999999999999.99m;
        var input = RuleInput.From(claim); var captured = JsonSerializer.Serialize(input);
        Assert.Equal("Accepted", RuleEvaluator.Evaluate(RuleCatalog.Baseline, input).Outcome);
        var definition = RuleCatalog.Baseline with { Version = "test-cap", Rules = RuleCatalog.Baseline.Rules.Select(r => r.Id == "AMOUNT_RANGE" ? r with { Max = 100 } : r).ToArray() };
        Assert.Equal("Rejected", RuleEvaluator.Evaluate(definition, input).Outcome);
        Assert.Equal(captured, JsonSerializer.Serialize(input));
        claim.ProcedureCodes.Clear(); Assert.Equal(2, input.Codes.Count);
        claim.Amount = 10000000000000000m; Assert.Contains(ClaimRules.Validate(claim), f => f.RuleId == "AMOUNT_RANGE");
    }
    [Theory]
    [InlineData("operator", "ExecuteScript")]
    [InlineData("field", "PrivateData")]
    [InlineData("when", "eval(true)")]
    [InlineData("alphabet", "Regex")]
    public void UnsupportedFieldsOperatorsAndParametersAreRejected(string property, string value)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(RuleCatalog.Baseline, RuleCatalog.Json))!;
        root["rules"]![0]![property] = value;
        Assert.Throws<ArgumentException>(() => RuleCatalog.Parse(root.ToJsonString()));
    }
    [Fact]
    public void DefinitionSchemaRejectsDuplicatesUnknownKeysInvalidWindowsAndLimits()
    {
        Assert.Throws<ArgumentException>(() => RuleCatalog.Parse("{\"version\":\"a\",\"Version\":\"b\"}"));
        var root = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(RuleCatalog.Baseline, RuleCatalog.Json))!;
        root["unexpected"] = true; Assert.Throws<JsonException>(() => RuleCatalog.Parse(root.ToJsonString())); root.AsObject().Remove("unexpected");
        root["effectiveUntil"] = root["effectiveFrom"]!.DeepClone(); Assert.Throws<ArgumentException>(() => RuleCatalog.Parse(root.ToJsonString()));
        root["effectiveUntil"] = null; root["rules"]![0]!["maxLength"] = 500; Assert.Throws<ArgumentException>(() => RuleCatalog.Parse(root.ToJsonString()));
        Assert.Throws<ArgumentException>(() => RuleCatalog.Parse(new string('x', 65537)));
    }
    [Fact]
    public void PlanScopedRulesAndCodeSetsAreDeterministic()
    {
        var definition = RuleCatalog.Baseline with { Version = "plan-v1", Rules = RuleCatalog.Baseline.Rules.Append(
            new("Claim", "PLAN_CODES", "ProcedureCodes", "CodesForbidden", "Use a different synthetic code.", Values: ["syn-a"], PlanTypes: ["SYN-PLUS"])).ToArray() };
        RuleCatalog.Validate(definition);
        var claim = ClaimRuleTests.Valid(); claim.PlanType = "SYN-BASIC";
        Assert.Empty(RuleEvaluator.Evaluate(definition, RuleInput.From(claim)).Failures);
        claim.PlanType = "SYN-PLUS";
        Assert.Equal("PLAN_CODES", Assert.Single(RuleEvaluator.Evaluate(definition, RuleInput.From(claim)).Failures).RuleId);
    }
    [Fact]
    public async Task CandidateIntegrityFailureIsAnObservationAndCannotChangeLiveValidation()
    {
        var live = RuleRegistry.Record(RuleCatalog.Baseline, DateTimeOffset.UtcNow);
        var corrupt = RuleRegistry.Record(RuleCatalog.Baseline with { Version = "bad-candidate" }, DateTimeOffset.UtcNow); corrupt.DefinitionJson = "{}";
        var evaluated = await RuleRegistry.EvaluateAsync(new(live, corrupt, 1), RuleInput.From(ClaimRuleTests.Valid()), DateTimeOffset.UtcNow, "Test");
        Assert.Equal("Accepted", evaluated.Live.Outcome); Assert.Empty(evaluated.Live.Failures); Assert.Equal("Error", evaluated.Candidate!.Outcome);
    }
}

public class RuleApiTests
{
    private static async Task<JsonElement> Registry(HttpClient client) => await client.GetFromJsonAsync<JsonElement>("/api/rules");
    private static async Task<long> Revision(HttpClient client) => (await Registry(client)).GetProperty("revision").GetInt64();
    private static async Task<HttpResponseMessage> Toggle(HttpClient client, string version, bool enabled) =>
        await client.PostAsJsonAsync($"/api/rules/{version}/shadow", new ShadowCommand(enabled, await Revision(client)));
    private static ClaimSubmission Claim(decimal amount = 150, string npi = "0000000006", string plan = "SYN-BASIC") =>
        new("CLM-RULE-" + Guid.NewGuid().ToString("N"), npi, "SYN-" + Guid.NewGuid().ToString("N"), new(2026, 1, 20), ["SYN-A", "syn-a", " SYN-B "], amount, PlanType: plan);
    private static async Task Register(HttpClient client, RuleDefinition definition)
    {
        using var response = await client.PostAsync("/api/rules", new StringContent(JsonSerializer.Serialize(definition, RuleCatalog.Json), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
    [Fact]
    public async Task ShadowRejectsWithoutChangingLiveStatusOutboxCorrectionsOrReplayAndDiffCountsDistinctCodes()
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Toggle(client, "candidate-v2", true)).StatusCode);
        var request = Claim(); using var keyed = factory.CreateDefaultClient(); keyed.DefaultRequestHeaders.Add("Idempotency-Key", "SYN-SHADOW-REPLAY");
        var response = await keyed.PostAsJsonAsync("/api/claims", request); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Equal(payload, await (await keyed.PostAsJsonAsync("/api/claims", request)).Content.ReadAsStringAsync());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
            var claim = await db.Claims.SingleAsync(c => c.Id == request.Id);
            Assert.Equal(ClaimStatus.PendingProcessing, claim.Status); Assert.Empty(claim.ValidationFailures);
            Assert.True(await db.Outbox.AnyAsync(o => o.ClaimId == request.Id)); Assert.False(await db.Corrections.AnyAsync(o => o.ClaimId == request.Id));
            var evaluation = await db.RuleEvaluations.SingleAsync(e => e.TransactionId == request.Id);
            Assert.Equal("Accepted", evaluation.LiveOutcome); Assert.Equal("Rejected", evaluation.CandidateOutcome); Assert.Equal("SYN-BASIC", evaluation.PlanType);
        }
        var diff = await client.GetFromJsonAsync<JsonElement>("/api/rules/candidate-v2/shadow-diff");
        Assert.Equal(1, diff.GetProperty("flipped").GetInt32()); Assert.Equal(1, diff.GetProperty("compared").GetInt32());
        Assert.Equal(2, diff.GetProperty("byProcedureCode").GetArrayLength()); Assert.All(diff.GetProperty("byProcedureCode").EnumerateArray(), r => Assert.Equal(1, r.GetProperty("flipped").GetInt32()));
        Assert.Equal(HttpStatusCode.OK, (await Toggle(client, "candidate-v2", false)).StatusCode);
        var off = Claim(); Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/claims", off)).StatusCode);
        using var check = factory.Services.CreateScope(); var saved = check.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        Assert.Null((await saved.RuleEvaluations.SingleAsync(e => e.TransactionId == off.Id)).CandidateVersion);
    }
    [Fact]
    public async Task ShadowAcceptingInvalidLiveClaimNeverBypassesRejectionAndCorrectionQueue()
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient();
        await Register(client, RuleCatalog.Baseline with { Version = "loose-npi", Rules = RuleCatalog.Baseline.Rules.Where(r => r.Id != "REQUIRED_PROVIDER_NPI").ToArray() });
        await Toggle(client, "loose-npi", true);
        var request = Claim(npi: ""); Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/claims", request)).StatusCode);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        Assert.Equal(ClaimStatus.Rejected, (await db.Claims.SingleAsync(c => c.Id == request.Id)).Status);
        Assert.True(await db.Corrections.AnyAsync(c => c.ClaimId == request.Id)); Assert.False(await db.Outbox.AnyAsync(o => o.ClaimId == request.Id));
        var evaluation = await db.RuleEvaluations.SingleAsync(e => e.TransactionId == request.Id);
        Assert.Equal("Rejected", evaluation.LiveOutcome); Assert.Equal("Accepted", evaluation.CandidateOutcome);
        var diff = await client.GetFromJsonAsync<JsonElement>("/api/rules/loose-npi/shadow-diff"); Assert.Equal(1, diff.GetProperty("rejectToAccept").GetInt32());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BothPriorAuthFormatsPreserveLiveAcceptanceAndSlaWhenCandidateRejects(bool x12)
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient(); await Toggle(client, "candidate-v2", true);
        var id = "PA-RULE-" + Guid.NewGuid().ToString("N"); var submitted = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var body = x12 ? PairedRequests.X12(id, "SYN-PA-RULE", "0000000006", ["SYN-REVIEW"], "U", submitted)
            : PairedRequests.Fhir(id, "SYN-PA-RULE", "0000000006", ["SYN-REVIEW"], "U", submitted, false);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/pa/" + (x12 ? "x12-278" : "fhir")) { Content = new StringContent(body, Encoding.UTF8, x12 ? "text/plain" : "application/fhir+json") };
        message.Headers.Add("X-Synthetic-Plan-Type", "SYN-PLUS");
        var response = await client.SendAsync(message); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await client.GetFromJsonAsync<JsonElement>("/api/pa/" + id); Assert.Equal("Accepted", saved.GetProperty("outcome").GetString());
        Assert.Equal("Open", saved.GetProperty("lifecycle").GetProperty("decision").GetString());
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        var evaluation = await db.RuleEvaluations.SingleAsync(e => e.TransactionId == id);
        Assert.Equal("Accepted", evaluation.LiveOutcome); Assert.Equal("Rejected", evaluation.CandidateOutcome); Assert.Equal("SYN-PLUS", evaluation.PlanType);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PriorAuthLiveRejectionCannotBeOverriddenByRelaxedCandidate(bool x12)
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient();
        await Register(client, RuleCatalog.Baseline with { Version = "pa-loose", Rules = RuleCatalog.Baseline.Rules.Where(r => r.Id != "PA_PROVIDER_NPI").ToArray() });
        await Toggle(client, "pa-loose", true);
        var id = "PA-REJECT-" + Guid.NewGuid().ToString("N");
        var body = x12 ? PairedRequests.X12(id, "SYN-RULE", "bad-npi", ["SYN-A"], "E", factory.Clock.UtcNow)
            : PairedRequests.Fhir(id, "SYN-RULE", "bad-npi", ["SYN-A"], "E", factory.Clock.UtcNow, false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/api/pa/" + (x12 ? "x12-278" : "fhir"), new StringContent(body, Encoding.UTF8, x12 ? "text/plain" : "application/fhir+json"))).StatusCode);
        var saved = await client.GetFromJsonAsync<JsonElement>("/api/pa/" + id); Assert.Equal("Rejected", saved.GetProperty("outcome").GetString()); Assert.Equal(JsonValueKind.Null, saved.GetProperty("lifecycle").ValueKind);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        var evaluation = await db.RuleEvaluations.SingleAsync(e => e.TransactionId == id); Assert.Equal("Accepted", evaluation.CandidateOutcome); Assert.Equal("Rejected", evaluation.LiveOutcome);
    }
    [Fact]
    public async Task X12837ShadowCannotChangeAcknowledgmentAndPromotedRulesDoApply()
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient(); await Toggle(client, "candidate-v2", true);
        var sample = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples/x12/valid.837"));
        var first = await client.PostAsync("/api/x12/837", new StringContent(sample, Encoding.UTF8, "text/plain")); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var id = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("interchanges")[0].GetProperty("parsedClaims")[0].GetProperty("claim").GetProperty("id").GetString()!;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>(); var evaluation = await db.RuleEvaluations.SingleAsync(e => e.TransactionId == id);
            Assert.Equal("Accepted", evaluation.LiveOutcome); Assert.Equal("Rejected", evaluation.CandidateOutcome);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/rules/candidate-v2/promote", new PromoteCommand(await Revision(client)))).StatusCode);
        var next = sample.Replace(id, "CLM-PROMOTED-837");
        var rejected = await client.PostAsync("/api/x12/837", new StringContent(next, Encoding.UTF8, "text/plain")); Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        var ack = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()).RootElement.GetProperty("interchanges")[0].GetProperty("acknowledgment");
        Assert.Equal("Rejected", ack.GetProperty("status").GetString());
        Assert.Contains(ack.GetProperty("errors").EnumerateArray(), e => e.GetProperty("segmentId").GetString() == "CLM" && e.GetProperty("elementPosition").GetInt32() == 2 && e.GetProperty("transactionPosition").GetInt32() == 5);
    }
    [Fact]
    public async Task CandidateErrorsPersistWithoutChangingLiveAndDefinitionControlChangesAreAudited()
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient(); await Registry(client);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
            var bad = RuleRegistry.Record(RuleCatalog.Baseline with { Version = "broken" }, factory.Clock.UtcNow); bad.DefinitionJson = "{}";
            db.RuleSets.Add(bad); await db.SaveChangesAsync();
            var control = await db.RuleControls.SingleAsync(); control.ShadowVersion = bad.Version; control.Revision++; await db.SaveChangesAsync();
        }
        var request = Claim(42); Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/claims", request)).StatusCode);
        using var check = factory.Services.CreateScope(); var saved = check.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        var evaluation = await saved.RuleEvaluations.SingleAsync(e => e.TransactionId == request.Id);
        Assert.Equal("Accepted", evaluation.LiveOutcome); Assert.Equal("Error", evaluation.CandidateOutcome);
        Assert.True(await saved.Audit.AnyAsync(e => e.EntityType == nameof(RuleControl) && e.Action == "Modified"));
        Assert.True(await saved.Audit.AnyAsync(e => e.EntityType == nameof(RuleEvaluationRecord) && e.EntityId == evaluation.Id));
        var diff = await client.GetFromJsonAsync<JsonElement>("/api/rules/broken/shadow-diff");
        Assert.Equal(1, diff.GetProperty("candidateErrors").GetInt32()); Assert.Equal(0, diff.GetProperty("compared").GetInt32());
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => saved.Database.ExecuteSqlRawAsync("UPDATE RuleEvaluations SET CandidateOutcome='Accepted'"));
    }
    [Fact]
    public async Task MalformedX12MappedTrafficIsExcludedFromFlipsAndPlanHeaderIsPartOfReplayIdentity()
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient(); await Toggle(client, "candidate-v2", true);
        var sample = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples/x12/valid.837"));
        var malformed = sample.Replace("SE*", "SE*999");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/api/x12/837", new StringContent(malformed, Encoding.UTF8, "text/plain"))).StatusCode);
        var diff = await client.GetFromJsonAsync<JsonElement>("/api/rules/candidate-v2/shadow-diff");
        Assert.True(diff.GetProperty("protocolRejected").GetInt32() > 0); Assert.Equal(0, diff.GetProperty("flipped").GetInt32());
        using var keyed = factory.CreateDefaultClient(); keyed.DefaultRequestHeaders.Add("Idempotency-Key", "SYN-PLAN-HEADER"); keyed.DefaultRequestHeaders.Add("X-Synthetic-Plan-Type", "SYN-BASIC");
        Assert.Equal(HttpStatusCode.OK, (await keyed.PostAsync("/api/x12/837", new StringContent(sample, Encoding.UTF8, "text/plain"))).StatusCode);
        keyed.DefaultRequestHeaders.Remove("X-Synthetic-Plan-Type"); keyed.DefaultRequestHeaders.Add("X-Synthetic-Plan-Type", "SYN-PLUS");
        Assert.Equal(HttpStatusCode.Conflict, (await keyed.PostAsync("/api/x12/837", new StringContent(sample, Encoding.UTF8, "text/plain"))).StatusCode);
    }
    [Fact]
    public async Task EffectiveWindowsPromotionHistoryAndVersionConflictsArePinned()
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient(); var start = factory.Clock.UtcNow.AddDays(1);
        var future = RuleCatalog.Baseline with { Version = "future", EffectiveFrom = start, EffectiveUntil = start.AddDays(1) }; await Register(client, future);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/rules/future/promote", new PromoteCommand(await Revision(client)))).StatusCode);
        await Toggle(client, "future", true);
        using var scope = factory.Services.CreateScope(); var registry = scope.ServiceProvider.GetRequiredService<RuleRegistry>();
        var pinned = await registry.SnapshotAsync(factory.Clock.UtcNow); Assert.Equal("phase0-v1", pinned.Live.Version); Assert.Equal("future", pinned.Candidate!.Version);
        factory.Clock.UtcNow = start;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/rules/future/promote", new PromoteCommand(await Revision(client)))).StatusCode);
        Assert.Equal("future", (await Registry(client)).GetProperty("activeVersion").GetString());
        Assert.Equal("phase0-v1", pinned.Live.Version);
        var request = Claim(42); var accepted = await client.PostAsJsonAsync("/api/claims", request); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal("future", JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()).RootElement.GetProperty("claim").GetProperty("ruleVersion").GetString());
        var conflict = await client.PostAsJsonAsync("/api/claims", Claim(42) with { RuleVersion = "phase0-v1" });
        Assert.Equal("ManualReview", JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()).RootElement.GetProperty("claim").GetProperty("status").GetString());
        factory.Clock.UtcNow = start.AddDays(1);
        Assert.Equal("phase0-v1", (await Registry(client)).GetProperty("activeVersion").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/rules/future/promote", new PromoteCommand(await Revision(client)))).StatusCode);
        Assert.Equal("future", (await client.GetFromJsonAsync<JsonElement>("/api/claims/" + request.Id)).GetProperty("claim").GetProperty("ruleVersion").GetString());
    }
    [Fact]
    public async Task MutationsRequireKeysAndRevisionsAndPromotionReplayAddsNoActivation()
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient(); using var noKey = factory.CreateDefaultClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await noKey.PostAsJsonAsync("/api/rules/candidate-v2/shadow", new ShadowCommand(true, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/rules/candidate-v2/shadow", new { enabled = true })).StatusCode);
        var revision = await Revision(client); await Toggle(client, "candidate-v2", true);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/rules/candidate-v2/promote", new PromoteCommand(revision))).StatusCode);
        using var keyed = factory.CreateDefaultClient(); keyed.DefaultRequestHeaders.Add("Idempotency-Key", "SYN-PROMOTE");
        var command = new PromoteCommand(await Revision(client));
        var response = await keyed.PostAsJsonAsync("/api/rules/candidate-v2/promote", command); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var replay = await keyed.PostAsJsonAsync("/api/rules/candidate-v2/promote", command); Assert.Equal(await response.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>(); Assert.Equal(1, await db.RuleActivations.CountAsync(r => r.Version == "candidate-v2"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE RuleSets SET ContentHash='changed'"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM RuleActivations"));
        var definition = await db.RuleSets.FirstAsync(); definition.DefinitionJson = "{}"; await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public async Task CompetingPromotionsOnlyOneRevisionCommits()
    {
        await using var factory = new SlaApiFactory(); using var client = factory.CreateClient();
        await Register(client, RuleCatalog.Baseline with { Version = "another" }); var revision = await Revision(client);
        async Task<int> Promote(string version)
        {
            using var scope = factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<RuleRegistry>().MutateAsync(version, null, revision, factory.Clock.UtcNow, default)).StatusCode;
        }
        var results = await Task.WhenAll(Promote("another"), Promote("candidate-v2")); Assert.Equal(new[] { 200, 409 }, results.Order());
    }
}
