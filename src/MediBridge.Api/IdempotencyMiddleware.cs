using System.Security.Cryptography;
using System.Text;
using MediBridge.Infrastructure;
using Microsoft.EntityFrameworkCore;

public sealed class IdempotencyGate { public SemaphoreSlim Gate { get; } = new(1, 1); }
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ClaimsDbContext db, IdempotencyGate gate, OutboxSignal signal, IFaultInjector faults)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) { await next(context); return; }
        var scope = (context.Request.Path.Value ?? "").TrimEnd('/').ToLowerInvariant();
        var parts = (context.Request.Path.Value ?? "").TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var isDecision = parts.Length == 4 && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("pa", StringComparison.OrdinalIgnoreCase) && parts[3].Equals("decisions", StringComparison.OrdinalIgnoreCase);
        // Hash the exact case-sensitive request ID into a bounded scope; intake scopes remain unchanged.
        if (isDecision) scope = "pa-decision:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts[2])))[..48];
        if (!isDecision && scope is not ("/api/claims" or "/api/x12/837" or "/api/pa/x12-278" or "/api/pa/fhir")) { await next(context); return; }
        var values = context.Request.Headers["Idempotency-Key"];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 128 || values[0]!.Any(c => c < '!' || c > '~'))
        { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { detail = "Provide one Idempotency-Key header containing 1–128 printable ASCII characters." }); return; }
        var key = values[0]!;
        var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (count == 0) break;
            if (body.Length + count > 65_536) { context.Response.StatusCode = 413; return; }
            await body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
        }
        var contentType = context.Request.ContentType ?? "";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contentType + "\n").Concat(body.ToArray()).ToArray()));
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "\n" + key)));
        await gate.Gate.WaitAsync(context.RequestAborted);
        var original = context.Response.Body;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                db.ChangeTracker.Clear();
                var previous = await db.Idempotency.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, context.RequestAborted);
                if (previous is not null)
                {
                    if (previous.Fingerprint != fingerprint) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { detail = "Idempotency key already used with a different request." }); return; }
                    context.Response.StatusCode = previous.StatusCode; context.Response.ContentType = string.IsNullOrEmpty(previous.ContentType) ? null : previous.ContentType;
                    if (previous.Location is not null) context.Response.Headers.Location = previous.Location;
                    context.Response.Headers["Idempotency-Replayed"] = "true";
                    await context.Response.WriteAsync(previous.Body, context.RequestAborted); return;
                }
                await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
                await using var capture = new MemoryStream();
                context.Response.Body = capture; body.Position = 0; context.Request.Body = body;
                try
                {
                    await next(context);
                    if (context.Response.StatusCode >= 500) { await transaction.RollbackAsync(context.RequestAborted); }
                    else
                    {
                        db.Idempotency.Add(new IdempotencyRecord
                        {
                            Id = id, Scope = scope, Key = key, Fingerprint = fingerprint,
                            StatusCode = context.Response.StatusCode, ContentType = context.Response.ContentType ?? "",
                            Location = context.Response.Headers.Location.FirstOrDefault(), Body = Encoding.UTF8.GetString(capture.ToArray()), CreatedAt = DateTime.UtcNow
                        });
                        await db.SaveChangesAsync(context.RequestAborted); await transaction.CommitAsync(context.RequestAborted);
                        signal.Wake(); await faults.CheckpointAsync("AfterClaimCommit", context.RequestAborted);
                    }
                    context.Response.Body = original; capture.Position = 0;
                    await capture.CopyToAsync(original, context.RequestAborted); return;
                }
                catch (DbUpdateException) when (attempt == 0)
                {
                    await transaction.RollbackAsync(context.RequestAborted); context.Response.Body = original;
                    context.Response.Clear();
                }
            }
        }
        finally { context.Response.Body = original; gate.Gate.Release(); await body.DisposeAsync(); }
    }
}
