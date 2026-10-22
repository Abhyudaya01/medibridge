using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediBridge.Core;
using MediBridge.Core.PriorAuth;
using MediBridge.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace MediBridge.Infrastructure.Rules;

public sealed record RuleSnapshot(RuleSetRecord Live, RuleSetRecord? Candidate, long Revision);
public sealed record EvaluatedRules(RuleResult Live, RuleResult? Candidate, RuleEvaluationRecord Record);
public sealed record RuleMutation(int StatusCode, object Body);
public sealed class RuleRegistry(ClaimsDbContext db)
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static RuleSetRecord Record(RuleDefinition definition, DateTimeOffset now)
    {
        RuleCatalog.Validate(definition); var json = JsonSerializer.Serialize(definition, RuleCatalog.Json);
        if (Encoding.UTF8.GetByteCount(json) > 65536) throw new ArgumentException("Normalized definition exceeds 64 KiB.");
        return new() { Version = definition.Version, DefinitionJson = json, ContentHash = Hash(json), EffectiveFromTicks = definition.EffectiveFrom.UtcTicks,
            EffectiveUntilTicks = definition.EffectiveUntil?.UtcTicks, CreatedAtTicks = now.UtcTicks };
    }
    public static async Task SeedAsync(ClaimsDbContext db, CancellationToken ct)
    {
        if (await db.RuleControls.AnyAsync(ct)) return;
        var baseline = Record(RuleCatalog.Baseline, DateTimeOffset.UtcNow);
        var candidateDefinition = RuleCatalog.Baseline with { Version = "candidate-v2", Rules = RuleCatalog.Baseline.Rules.Select(r => r.Id == "AMOUNT_RANGE"
            ? r with { Max = 100m, Fix = "Use an amount no greater than 100 for this synthetic candidate policy." } : r).Append(
                new("PriorAuth", "DEMO_SERVICE_REVIEW", "ServiceCodes", "CodesForbidden", "Synthetic candidate requires review for SYN-REVIEW.", Values: ["SYN-REVIEW"])).ToArray() };
        db.RuleSets.AddRange(baseline, Record(candidateDefinition, DateTimeOffset.UtcNow));
        db.RuleControls.Add(new()); db.RuleActivations.Add(new() { Version = baseline.Version, ActivatedAtTicks = 0 });
        await db.SaveChangesAsync(ct);
    }
    public async Task<RuleSnapshot> SnapshotAsync(DateTimeOffset receipt, CancellationToken ct = default)
    {
        // Activations are immutable and bounded by the pinned control revision, even across concurrent promotions.
        var control = await db.RuleControls.AsNoTracking().SingleAsync(ct);
        var live = await (from activation in db.RuleActivations.AsNoTracking()
            join definition in db.RuleSets.AsNoTracking() on activation.Version equals definition.Version
            where activation.Revision <= control.Revision && activation.ActivatedAtTicks <= receipt.UtcTicks && definition.EffectiveFromTicks <= receipt.UtcTicks
                && (definition.EffectiveUntilTicks == null || receipt.UtcTicks < definition.EffectiveUntilTicks)
            orderby activation.Revision descending, activation.Id descending select definition).FirstOrDefaultAsync(ct);
        if (live is null) throw new InvalidOperationException("No promoted, effective rule version is available.");
        var candidate = control.ShadowVersion is null || control.ShadowVersion == live.Version ? null
            : await db.RuleSets.AsNoTracking().SingleAsync(r => r.Version == control.ShadowVersion, ct);
        return new(live, candidate, control.Revision);
    }
    private static RuleDefinition Definition(RuleSetRecord record)
    {
        if (Hash(record.DefinitionJson) != record.ContentHash) throw new ArgumentException("Definition hash mismatch.");
        var definition = RuleCatalog.Parse(record.DefinitionJson);
        if (definition.Version != record.Version) throw new ArgumentException("Definition version mismatch.");
        return definition;
    }
    public static async Task<EvaluatedRules> EvaluateAsync(RuleSnapshot snapshot, RuleInput input, DateTimeOffset receipt,
        string source, bool protocolAccepted = true, CancellationToken ct = default)
    {
        var liveTask = Task.Run(() => RuleEvaluator.Evaluate(Definition(snapshot.Live), input), ct);
        var shadowTask = Task.Run(() =>
        {
            if (snapshot.Candidate is null) return null;
            try { return RuleEvaluator.Evaluate(Definition(snapshot.Candidate), input); }
            catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException)
            { return new RuleResult(snapshot.Candidate.Version, [], "Candidate definition could not be evaluated; live result is unchanged."); }
        }, ct);
        await Task.WhenAll((Task)liveTask, (Task)shadowTask);
        var live = await liveTask; var candidate = await shadowTask;
        return new(live, candidate, new() { TransactionId = input.Id, Domain = input.Domain, Source = source, ReceivedAtTicks = receipt.UtcTicks,
            Provider = input.Provider.Length <= 200 ? input.Provider : "Unspecified", CodesJson = JsonSerializer.Serialize(input.Codes.Select(c => (c ?? "").Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)), PlanType = input.PlanType,
            LiveVersion = snapshot.Live.Version, LiveHash = snapshot.Live.ContentHash, LiveJson = JsonSerializer.Serialize(live), LiveOutcome = live.Outcome,
            CandidateVersion = snapshot.Candidate?.Version, CandidateHash = snapshot.Candidate?.ContentHash, CandidateJson = candidate is null ? null : JsonSerializer.Serialize(candidate), CandidateOutcome = candidate?.Outcome, ProtocolAccepted = protocolAccepted });
    }
    public async Task<object> ListAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var snapshot = await SnapshotAsync(now, ct);
        var records = await db.RuleSets.AsNoTracking().OrderBy(r => r.Version).ToListAsync(ct);
        return new { activeVersion = snapshot.Live.Version, shadowVersion = snapshot.Candidate?.Version, revision = snapshot.Revision, asOf = now,
            versions = records.Select(r => new { r.Version, r.ContentHash, effectiveFrom = new DateTimeOffset(r.EffectiveFromTicks, TimeSpan.Zero), effectiveUntil = r.EffectiveUntilTicks.HasValue ? (DateTimeOffset?)new(r.EffectiveUntilTicks.Value, TimeSpan.Zero) : null,
                definition = JsonSerializer.Deserialize<JsonElement>(r.DefinitionJson), definitionJson = r.DefinitionJson, eligibleNow = r.EffectiveFromTicks <= now.UtcTicks && (r.EffectiveUntilTicks is null || now.UtcTicks < r.EffectiveUntilTicks),
                active = r.Version == snapshot.Live.Version, shadow = r.Version == snapshot.Candidate?.Version }) };
    }
    public async Task<RuleMutation> RegisterAsync(string json, DateTimeOffset now, CancellationToken ct)
    {
        RuleDefinition definition;
        try { definition = RuleCatalog.Parse(json); }
        catch (Exception e) when (e is JsonException or ArgumentException) { return new(400, new { detail = "Invalid rule definition: " + (e is JsonException ? "Malformed JSON or unsupported properties." : e.Message) }); }
        if (await db.RuleSets.AnyAsync(r => r.Version == definition.Version, ct)) return new(409, new { detail = "Version already exists; register a new immutable version." });
        RuleSetRecord record;
        try { record = Record(definition, now); }
        catch (ArgumentException e) { return new(400, new { detail = e.Message }); }
        db.RuleSets.Add(record); await db.SaveChangesAsync(ct);
        return new(201, new { record.Version, record.ContentHash });
    }
    public async Task<RuleMutation> MutateAsync(string version, bool? shadow, long expectedRevision, DateTimeOffset now, CancellationToken ct)
    {
        var record = await db.RuleSets.AsNoTracking().SingleOrDefaultAsync(r => r.Version == version, ct);
        if (record is null) return new(404, new { detail = "Unknown rule version." });
        var control = await db.RuleControls.SingleAsync(ct);
        if (control.Revision != expectedRevision) return new(409, new { detail = "Rule control changed; reload its revision and retry with a new idempotency key." });
        var snapshot = await SnapshotAsync(now, ct);
        if (shadow is true)
        {
            if (snapshot.Live.Version == version) return new(409, new { detail = "The active version cannot shadow itself." });
            if (control.ShadowVersion is not null && control.ShadowVersion != version) return new(409, new { detail = "Disable the current candidate before enabling another." });
            try { Definition(record); } catch (Exception e) when (e is ArgumentException or JsonException) { return new(409, new { detail = "Candidate definition failed integrity validation." }); }
            if (control.ShadowVersion == version) return new(200, new { version, enabled = true, revision = control.Revision });
            control.ShadowVersion = version;
        }
        else if (shadow is false)
        {
            if (control.ShadowVersion != version) return new(200, new { version, enabled = false, revision = control.Revision });
            control.ShadowVersion = null;
        }
        else
        {
            if (now.UtcTicks < record.EffectiveFromTicks || record.EffectiveUntilTicks is { } end && now.UtcTicks >= end)
                return new(409, new { detail = "Promotion requires a version effective at the current receipt clock." });
            try { Definition(record); } catch (Exception e) when (e is ArgumentException or JsonException) { return new(409, new { detail = "Definition failed integrity validation." }); }
            if (snapshot.Live.Version == version) return new(200, new { version, revision = control.Revision });
            db.RuleActivations.Add(new() { Version = version, ActivatedAtTicks = now.UtcTicks, Revision = control.Revision + 1 });
            if (control.ShadowVersion == version) control.ShadowVersion = null;
        }
        control.Revision++;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return new(409, new { detail = "Concurrent rule control change; reload and retry." }); }
        return new(200, new { version, enabled = shadow, revision = control.Revision });
    }
    public async Task<object?> DiffAsync(string version, CancellationToken ct)
    {
        if (!await db.RuleSets.AnyAsync(r => r.Version == version, ct)) return null;
        var comparisons = await db.RuleEvaluations.AsNoTracking().Where(e => e.CandidateVersion == version).ToListAsync(ct);
        var comparable = comparisons.Where(e => e.ProtocolAccepted && e.CandidateOutcome != "Error").ToArray();
        var flipped = comparable.Where(e => e.LiveOutcome != e.CandidateOutcome).ToArray();
        object[] Group(Func<RuleEvaluationRecord, IEnumerable<string>> keys) => comparable.SelectMany(e => keys(e).Distinct(StringComparer.Ordinal).Select(k => (Key: k, Row: e)))
            .GroupBy(e => (e.Row.Domain, e.Key)).OrderBy(g => g.Key.Domain).ThenBy(g => g.Key.Key, StringComparer.Ordinal)
            .Select(g => (object)new { domain = g.Key.Domain, value = g.Key.Key, compared = g.Count(), flipped = g.Count(x => x.Row.LiveOutcome != x.Row.CandidateOutcome),
                acceptToReject = g.Count(x => x.Row.LiveOutcome == "Accepted" && x.Row.CandidateOutcome == "Rejected"), rejectToAccept = g.Count(x => x.Row.LiveOutcome == "Rejected" && x.Row.CandidateOutcome == "Accepted") }).ToArray();
        return new { version, observed = comparisons.Count, compared = comparable.Length, flipped = flipped.Length,
            candidateErrors = comparisons.Count(e => e.CandidateOutcome == "Error"), protocolRejected = comparisons.Count(e => !e.ProtocolAccepted),
            acceptToReject = flipped.Count(e => e.LiveOutcome == "Accepted"), rejectToAccept = flipped.Count(e => e.LiveOutcome == "Rejected"),
            byProvider = Group(e => [string.IsNullOrEmpty(e.Provider) ? "Unspecified" : e.Provider]),
            byProcedureCode = Group(e => JsonSerializer.Deserialize<string[]>(e.CodesJson) is { Length: > 0 } codes ? codes : ["Unspecified"]), byPlanType = Group(e => [e.PlanType]),
            baselineVersions = comparable.Select(e => e.LiveVersion).Distinct().Order().ToArray(),
            measurement = "Paired intake-validation verdicts for newly evaluated mapped transactions; code groups overlap. Replays do not add observations; candidate errors and protocol rejections are excluded from flip counts." };
    }
}
