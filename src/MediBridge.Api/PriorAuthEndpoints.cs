using System.Text;
using MediBridge.Core.PriorAuth;
using MediBridge.Infrastructure.PriorAuth;
using Microsoft.Net.Http.Headers;

public static class PriorAuthEndpoints
{
    public static void MapPriorAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/pa/x12-278", (HttpRequest request, PriorAuthStore store, TimeProvider clock, CancellationToken ct) => Intake(request, store, clock, false, ct))
            .Accepts<string>("text/plain").Produces<PriorAuthResponse>(201).Produces(400).Produces(422).Produces(409)
            .WithSummary("Import one synthetic 278 request using the documented segment subset.");
        app.MapPost("/api/pa/fhir", (HttpRequest request, PriorAuthStore store, TimeProvider clock, CancellationToken ct) => Intake(request, store, clock, true, ct))
            .Accepts<string>("application/fhir+json", "application/json").Produces<PriorAuthResponse>(201).Produces(400).Produces(422).Produces(409)
            .WithSummary("Import a FHIR R4 Claim (use=preauthorization) using the PAS-shaped synthetic subset.");
        app.MapGet("/api/pa/{id}", async (string id, PriorAuthStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } response ? Results.Ok(response) : Results.NotFound());
    }
    private static async Task<IResult> Intake(HttpRequest request, PriorAuthStore store, TimeProvider clock, bool fhir, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media)
            || (fhir ? !media.MediaType.Equals("application/fhir+json", StringComparison.OrdinalIgnoreCase) && !media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                     : !media.MediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
            || media.Charset.HasValue && !media.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)) return Results.StatusCode(415);
        string text;
        try { using var reader = new StreamReader(request.Body, new UTF8Encoding(false, true), false); text = await reader.ReadToEndAsync(ct); }
        catch (DecoderFallbackException) { return Results.BadRequest(new { outcome = "Rejected", errors = new[] { new PriorAuthError("PA_UTF8", "Input", "Use valid UTF-8 text.") } }); }
        var result = fhir ? FhirPriorAuthMapper.Map(text) : X12278Mapper.Map(text);
        if (result.Request is null || result.Errors.Any(e => e.Field == "Id")) return Results.BadRequest(result);
        var response = await store.SubmitAsync(result, fhir ? "FHIR-R4" : "X12-278", clock.GetUtcNow(), ct);
        if (response is null) return Results.Conflict(new { outcome = "Rejected", errors = new[] { new PriorAuthError("PA_DUPLICATE_ID", "Id", "This request ID already exists. GET its timeline or submit a new synthetic ID.") } });
        return response.Outcome == "Accepted" ? Results.Created($"/api/pa/{Uri.EscapeDataString(response.Request.Id)}", response) : Results.UnprocessableEntity(response);
    }
}
