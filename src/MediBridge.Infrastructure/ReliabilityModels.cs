namespace MediBridge.Infrastructure;

public sealed class OutboxMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClaimId { get; set; } = "";
    public string State { get; set; } = "Pending";
    public int Deliveries { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public long Version { get; set; }
}
public sealed class HandlerReceipt
{
    public string Id { get; set; } = "";
    public DateTime CompletedAt { get; set; }
}
public sealed class CorrectionItem
{
    public string ClaimId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string Reason { get; set; } = "";
}
public sealed class IdempotencyRecord
{
    public string Id { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Key { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int StatusCode { get; set; }
    public string Body { get; set; } = "";
    public string? Location { get; set; }
    public string ContentType { get; set; } = "application/json; charset=utf-8";
    public DateTime CreatedAt { get; set; }
}
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
}
