using System.Text.Json;
using System.Text.Json.Serialization;
using MediBridge.Core.PriorAuth;
using Microsoft.EntityFrameworkCore;

namespace MediBridge.Infrastructure.PriorAuth;

public sealed class PriorAuthRecord
{
    public string Id { get; set; } = "";
    public string CanonicalJson { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string SourceFormat { get; set; } = "";
    public string ValidationJson { get; set; } = "";
    public string? LifecycleJson { get; set; }
    public long? NextSlaCheckTicks { get; set; }
    public long Version { get; set; }
}
public sealed class PriorAuthEvent
{
    public long Id { get; set; }
    public string RequestId { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public string Stage { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? StateJson { get; set; }
}
public sealed record PriorAuthResponse(PriorAuthRequest Request, string Outcome, IReadOnlyList<PriorAuthError> Errors, IReadOnlyList<PriorAuthEvent> Timeline, PriorAuthLifecycle? Lifecycle = null);
public static class PriorAuthJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
public sealed class PriorAuthStore(ClaimsDbContext db)
{
    public async Task<PriorAuthResponse?> SubmitAsync(PriorAuthMapping mapping, string format, DateTimeOffset receivedAt, CancellationToken ct = default)
    {
        var canonical = mapping.Request ?? throw new ArgumentException("Only mapped requests can be stored.");
        if (PriorAuthRules.Validate(canonical).Any(e => e.Field == "Id")) throw new ArgumentException("A valid request ID is required.");
        if (await db.PriorAuthRequests.AnyAsync(r => r.Id == canonical.Id, ct)) return null;
        var record = new PriorAuthRecord { Id = canonical.Id, CanonicalJson = JsonSerializer.Serialize(canonical, PriorAuthJson.Options),
            Outcome = mapping.Outcome, SourceFormat = format, ValidationJson = JsonSerializer.Serialize(mapping.Errors, PriorAuthJson.Options) };
        var lifecycle = mapping.Outcome == "Accepted" ? new PriorAuthLifecycle(canonical.Priority!.Value, receivedAt.ToUniversalTime()) : null;
        if (lifecycle is not null) PriorAuthLifecycleStore.Set(record, lifecycle);
        db.PriorAuthRequests.Add(record);
        var timeline = new[]
        {
            Event("Intake", "Received", $"Synthetic {format} prior-authorization intake received."),
            Event("Mapping", "Mapped", "Mapped to the canonical prior-authorization model; source submission time normalized to UTC."),
            Event("Validation", mapping.Outcome, mapping.Outcome == "Accepted" ? "Intake accepted. No authorization or coverage decision has been made." : "Intake rejected by shared canonical validation; correct the reported fields.")
        };
        if (lifecycle is not null) timeline[^1].StateJson = PriorAuthLifecycleStore.Json(lifecycle);
        db.PriorAuthEvents.AddRange(timeline);
        await db.SaveChangesAsync(ct);
        return new(canonical, record.Outcome, mapping.Errors, timeline, lifecycle);
        PriorAuthEvent Event(string stage, string outcome, string detail) => new() { RequestId = canonical.Id, Timestamp = receivedAt.ToUniversalTime(), Stage = stage, Outcome = outcome, Detail = detail };
    }
    public async Task<PriorAuthResponse?> GetAsync(string id, CancellationToken ct = default)
    {
        var record = await db.PriorAuthRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (record is null) return null;
        return new(JsonSerializer.Deserialize<PriorAuthRequest>(record.CanonicalJson, PriorAuthJson.Options)!, record.Outcome,
            JsonSerializer.Deserialize<List<PriorAuthError>>(record.ValidationJson, PriorAuthJson.Options)!,
            await db.PriorAuthEvents.AsNoTracking().Where(e => e.RequestId == id).OrderBy(e => e.Id).ToListAsync(ct), PriorAuthLifecycleStore.Read(record));
    }
}
