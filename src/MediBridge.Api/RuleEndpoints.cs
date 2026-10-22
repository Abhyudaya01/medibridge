using System.Text;
using MediBridge.Core.PriorAuth;
using MediBridge.Infrastructure.Rules;

public static class RuleEndpoints
{
    public static void MapRuleEndpoints(this WebApplication app)
    {
        app.MapGet("/api/rules", async (RuleRegistry rules, IClock clock, CancellationToken ct) => Results.Ok(await rules.ListAsync(clock.UtcNow, ct)));
        app.MapPost("/api/rules", async (HttpRequest request, RuleRegistry rules, IClock clock, CancellationToken ct) =>
        {
            if (request.ContentType?.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) != true) return Results.StatusCode(415);
            using var reader = new StreamReader(request.Body, new UTF8Encoding(false, true));
            string json;
            try { json = await reader.ReadToEndAsync(ct); }
            catch (DecoderFallbackException) { return Results.BadRequest(new { detail = "Use valid UTF-8 JSON." }); }
            var result = await rules.RegisterAsync(json, clock.UtcNow, ct);
            return Results.Json(result.Body, statusCode: result.StatusCode);
        });
        app.MapPost("/api/rules/{version}/shadow", async (string version, ShadowCommand command, RuleRegistry rules, IClock clock, CancellationToken ct) =>
        {
            if (command.Enabled is null || command.ExpectedRevision is null) return Results.BadRequest(new { detail = "Provide enabled and expectedRevision from GET /api/rules." });
            var result = await rules.MutateAsync(version, command.Enabled, command.ExpectedRevision.Value, clock.UtcNow, ct);
            return Results.Json(result.Body, statusCode: result.StatusCode);
        });
        app.MapGet("/api/rules/{version}/shadow-diff", async (string version, RuleRegistry rules, CancellationToken ct) =>
            await rules.DiffAsync(version, ct) is { } diff ? Results.Ok(diff) : Results.NotFound());
        app.MapPost("/api/rules/{version}/promote", async (string version, PromoteCommand command, RuleRegistry rules, IClock clock, CancellationToken ct) =>
        {
            if (command.ExpectedRevision is null) return Results.BadRequest(new { detail = "Provide expectedRevision from GET /api/rules." });
            var result = await rules.MutateAsync(version, null, command.ExpectedRevision.Value, clock.UtcNow, ct);
            return Results.Json(result.Body, statusCode: result.StatusCode);
        });
    }
}
public sealed record ShadowCommand(bool? Enabled, long? ExpectedRevision);
public sealed record PromoteCommand(long? ExpectedRevision);
