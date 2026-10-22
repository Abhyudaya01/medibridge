using MediBridge.Core.PriorAuth;
using MediBridge.Infrastructure;
using MediBridge.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Routing;

public sealed class MonitoringMiddleware(RequestDelegate next, ILogger<MonitoringMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IServiceScopeFactory scopes, IClock clock)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/api/monitoring", StringComparison.OrdinalIgnoreCase))
        { await next(context); return; }
        var failed = false;
        try { await next(context); }
        catch { failed = true; throw; }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "/api/unmatched";
            var method = context.Request.Method.ToUpperInvariant();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
                db.EndpointObservations.Add(new EndpointObservation
                {
                    CompletedAtTicks = clock.UtcNow.UtcTicks, Method = method.Length <= 16 ? method : "OTHER", Route = route,
                    StatusCode = failed ? 500 : context.Response.StatusCode, Exception = failed, Aborted = context.RequestAborted.IsCancellationRequested,
                    Replayed = context.Response.Headers["Idempotency-Replayed"] == "true"
                });
                await db.SaveChangesAsync(timeout.Token);
            }
            catch (Exception error)
            {
                // Operational telemetry is best effort; it must never change a committed claim or its response.
                logger.LogWarning(error, "Endpoint monitoring could not be persisted for {Method} {Route}.", method, route);
            }
        }
    }
}
