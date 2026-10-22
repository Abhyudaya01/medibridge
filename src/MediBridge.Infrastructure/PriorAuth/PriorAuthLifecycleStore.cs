using System.Text.Json;
using MediBridge.Core.PriorAuth;
using Microsoft.EntityFrameworkCore;

namespace MediBridge.Infrastructure.PriorAuth;

public sealed class SystemClock(TimeProvider time) : IClock { public DateTimeOffset UtcNow => time.GetUtcNow(); }
public sealed record PriorAuthBoardItem(string Id, PriorAuthLifecycle Lifecycle);
public sealed record DecisionResult(int StatusCode, PriorAuthResponse? Response = null, DecisionError? Error = null);

public sealed class PriorAuthLifecycleStore(ClaimsDbContext db, IClock clock)
{
    public static PriorAuthLifecycle? Read(PriorAuthRecord record) => record.LifecycleJson is null ? null
        : JsonSerializer.Deserialize<PriorAuthLifecycle>(record.LifecycleJson, PriorAuthJson.Options)!;
    public static string Json(PriorAuthLifecycle state) => JsonSerializer.Serialize(state, PriorAuthJson.Options);
    public static void Set(PriorAuthRecord record, PriorAuthLifecycle state)
    {
        record.LifecycleJson = Json(state); record.NextSlaCheckTicks = state.NextCheckAt?.UtcTicks;
    }
    private void Event(PriorAuthRecord record, PriorAuthLifecycle state, string stage, string outcome, DateTimeOffset now, string detail)
    {
        Set(record, state);
        db.PriorAuthEvents.Add(new() { RequestId = record.Id, Timestamp = now, Stage = stage, Outcome = outcome, Detail = detail, StateJson = Json(state) });
    }
    private async Task<PriorAuthLifecycle> Initialize(PriorAuthRecord record, DateTimeOffset now, CancellationToken ct)
    {
        if (Read(record) is { } state) return state;
        var receipt = await db.PriorAuthEvents.Where(e => e.RequestId == record.Id && e.Stage == "Intake" && e.Outcome == "Received").OrderBy(e => e.Id).FirstAsync(ct);
        var canonical = JsonSerializer.Deserialize<PriorAuthRequest>(record.CanonicalJson, PriorAuthJson.Options)!;
        state = new(canonical.Priority!.Value, receipt.Timestamp.ToUniversalTime());
        Event(record, state, "SlaClock", "Started", now, "Existing accepted intake enrolled using its original receipt timestamp; pending never pauses the clock.");
        return state;
    }
    private PriorAuthLifecycle Escalate(PriorAuthRecord record, PriorAuthLifecycle state, DateTimeOffset now)
    {
        foreach (var next in PriorAuthLifecycleRules.Escalations(state, now))
        {
            Event(record, next, "Escalation", next.Sla.ToString(), now,
                next.Sla == PriorAuthSla.AtRisk ? "80% of the decision window reached; review before the deadline." : "100% of the decision window reached; deadline breached.");
            state = next;
        }
        return state;
    }
    // One optimistic update and its audit events commit atomically. Competing sweepers retry from durable state.
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var now = clock.UtcNow;
            var records = await db.PriorAuthRequests.Where(r => r.Outcome == "Accepted" && (r.LifecycleJson == null || r.NextSlaCheckTicks <= now.UtcTicks))
                .OrderBy(r => r.Id).Take(200).ToListAsync(ct);
            if (records.Count == 0) return 0;
            foreach (var record in records) Escalate(record, await Initialize(record, now, ct), now);
            try { await db.SaveChangesAsync(ct); return records.Count; }
            catch (DbUpdateConcurrencyException) when (attempt < 4) { db.ChangeTracker.Clear(); }
        }
    }
    public async Task<DecisionResult> DecideAsync(string id, DecisionCommand command, CancellationToken ct = default)
    {
        var record = await db.PriorAuthRequests.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (record is null) return new(404);
        if (record.Outcome != "Accepted") return new(409, Error: new("PA_INTAKE_REJECTED", "Id", "Only accepted intake can receive a decision."));
        var now = clock.UtcNow;
        // Existing records are enrolled at startup; fallback is for callers bypassing API startup.
        var state = await Initialize(record, now, ct);
        if (PriorAuthLifecycleRules.Validate(state, command, now) is { } error)
        { db.ChangeTracker.Clear(); return new(error.Code is "PA_FINAL" or "PA_TRANSITION" or "PA_CLOCK" ? 409 : 422, Error: error); }
        state = Escalate(record, state, now);
        var next = PriorAuthLifecycleRules.Decide(state, command, now);
        Event(record, next, "Decision", next.Decision.ToString(), now, next.ReasonCode is { } reason ? $"Synthetic denial reason: {reason}." : "Synthetic review decision recorded.");
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return new(409, Error: new("PA_CONCURRENT_CHANGE", "Decision", "Request changed concurrently; reload and retry with a new idempotency key.")); }
        return new(200, await new PriorAuthStore(db).GetAsync(id, ct));
    }
    public async Task<object> BoardAsync(string? after, int limit, CancellationToken ct = default)
    {
        var query = db.PriorAuthRequests.AsNoTracking().Where(r => r.Outcome == "Accepted" && r.LifecycleJson != null);
        // Finalized rows are omitted after deserialization; paging remains stable by case-sensitive ID.
        var records = await query.OrderBy(r => r.Id).ToListAsync(ct);
        var active = records.Select(r => new PriorAuthBoardItem(r.Id, Read(r)!)).Where(r => r.Lifecycle is not null && !r.Lifecycle.IsFinal).ToArray();
        var page = active.Where(r => after is null || string.CompareOrdinal(r.Id, after) > 0).OrderBy(r => r.Id, StringComparer.Ordinal).Take(limit + 1).ToArray();
        return new { asOf = clock.UtcNow, counts = new { onTrack = active.Count(r => r.Lifecycle.Sla == PriorAuthSla.OnTrack), atRisk = active.Count(r => r.Lifecycle.Sla == PriorAuthSla.AtRisk), breached = active.Count(r => r.Lifecycle.Sla == PriorAuthSla.Breached) },
            requests = page.Take(limit).ToArray(), next = page.Length > limit ? page[limit - 1].Id : null };
    }
    public async Task<object> MetricsAsync(CancellationToken ct = default)
    {
        var events = await db.PriorAuthEvents.AsNoTracking().Where(e => e.StateJson != null).OrderBy(e => e.Id)
            .Select(e => new { e.RequestId, e.StateJson }).ToListAsync(ct);
        return new { asOf = clock.UtcNow, rateDenominator = "Finalized accepted intakes; rates are fractions 0–1", turnaroundUnit = "hours", source = "Append-only PriorAuthEvents",
            metrics = PriorAuthMetricMath.Compute(events.Select(e => new PriorAuthMetricObservation(e.RequestId, JsonSerializer.Deserialize<PriorAuthLifecycle>(e.StateJson!, PriorAuthJson.Options)!))) };
    }
}
