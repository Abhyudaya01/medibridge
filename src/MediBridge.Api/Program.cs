using System.Text.Json.Serialization;
using MediBridge.Core;
using MediBridge.Infrastructure;
using MediBridge.Infrastructure.PriorAuth;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
{
    Title = "MediBridge synthetic claims API", Version = "0.6.0",
    Description = "Synthetic data only. Not affiliated with any payer. No real member or provider data."
}));
builder.Services.AddSingleton<ClaimWriteLock>();
builder.Services.AddScoped<ClaimStore>();
builder.Services.AddScoped<X12Intake>();
builder.Services.AddScoped<PriorAuthStore>();
builder.Services.AddSingleton(TimeProvider.System);
var faultOptions = builder.Configuration.GetSection("FaultInjection").Get<FaultOptions>() ?? new();
if (faultOptions.TransientFailureRate is < 0 or > 1 || double.IsNaN(faultOptions.TransientFailureRate)
    || faultOptions.AddedLatencyMs is < 0 or > 10000
    || (faultOptions.CrashAfterStep is not null && faultOptions.CrashAfterStep is not ("AfterClaimCommit" or "AfterLease" or "AfterAttemptStarted" or "AfterHandlerCommit")))
    throw new InvalidOperationException("Invalid fault injection configuration.");
builder.Services.AddSingleton<IFaultInjector>(new ConfigurableFaultInjector(faultOptions));
builder.Services.AddSingleton<OutboxDispatcher>();
builder.Services.AddSingleton<OutboxSignal>();
builder.Services.AddSingleton<IdempotencyGate>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddDbContext<SqliteClaimsDbContext>(options =>
{
    var onAppService = OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
    var path = builder.Configuration["Database:Path"]
        ?? (onAppService ? "/home/data/medibridge.db" : Path.Combine(builder.Environment.ContentRootPath, "Data", "medibridge.db"));
    path = Path.GetFullPath(path, builder.Environment.ContentRootPath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString());
});
builder.Services.AddDbContext<SqlClaimsDbContext>(options => options.UseSqlServer(builder.Configuration.GetConnectionString("Sql")));
builder.Services.AddScoped<ClaimsDbContext>(services => string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Sql"))
    ? services.GetRequiredService<SqliteClaimsDbContext>() : services.GetRequiredService<SqlClaimsDbContext>());

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ClaimsDbContext>().InitializeAsync();
    if (builder.Configuration.GetValue("Seed:Enabled", true))
        await SyntheticSeed.RunAsync(scope.ServiceProvider.GetRequiredService<ClaimStore>());
}

app.UseMiddleware<IdempotencyMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/health", () => Results.Ok(new { status = "ok", version = "0.6.0" }));
app.MapGet("/api/claims", async (ClaimsDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await db.Claims.AsNoTracking().OrderBy(c => c.Id).ToListAsync(cancellationToken)));

app.MapGet("/api/claims/{id}", async (string id, ClaimsDbContext db, CancellationToken cancellationToken) =>
{
    var claim = await db.Claims.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
    if (claim is null) return Results.NotFound();
    var timeline = await db.ClaimEvents.AsNoTracking().Where(e => e.ClaimId == id).OrderBy(e => e.Id).ToListAsync(cancellationToken);
    return Results.Ok(new ClaimResponse(claim, timeline, claim.ValidationFailures));
});

app.MapPost("/api/claims", async (ClaimSubmission request, ClaimStore store, CancellationToken cancellationToken) =>
{
    var claim = new Claim
    {
        Id = request.Id ?? "", ProviderNpi = request.ProviderNpi ?? "", MemberId = request.MemberId ?? "",
        ServiceDate = request.ServiceDate, ProcedureCodes = request.ProcedureCodes ?? [],
        Amount = request.Amount, RuleVersion = request.RuleVersion ?? ClaimRules.Version
    };
    // Invalid identities cannot be persisted; other business failures are retained as rejected claims.
    if (!Enum.IsDefined(request.Simulation))
        return Results.BadRequest(new { validation = new[] { new ValidationFailure("SIMULATION_SUPPORTED", "Simulation", "Use None or DownstreamTimeout.") } });
    if (string.IsNullOrWhiteSpace(claim.Id) || claim.Id.Length > 64 || claim.ProviderNpi.Length > 200
        || claim.MemberId.Length > 200 || claim.RuleVersion.Length > 200)
        return Results.BadRequest(new { validation = ClaimRules.Validate(claim), detail = "Supply an ID (1–64 characters), fields up to 200 characters, and a supported simulation." });
    var result = await store.SubmitAsync(claim, request.Simulation, DateTime.UtcNow, cancellationToken);
    if (result is null)
        return Results.Conflict(new { validation = new[] { new ValidationFailure("DUPLICATE_ID", "Id", "Fetch the existing claim, or use a new synthetic claim ID.") } });
    var response = new ClaimResponse(result.Claim, result.Timeline, result.Claim.ValidationFailures);
    return claim.Status switch
    {
        ClaimStatus.Rejected => Results.UnprocessableEntity(response),
        ClaimStatus.Suppressed => Results.Ok(response),
        ClaimStatus.ManualReview => Results.Accepted($"/api/claims/{Uri.EscapeDataString(claim.Id)}", response),
        _ => Results.Accepted($"/api/claims/{Uri.EscapeDataString(claim.Id)}", response)
    };
});

app.MapPost("/api/x12/837", async (HttpRequest request, X12Intake intake, CancellationToken cancellationToken) =>
{
    if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
        || !contentType.MediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase)
        || (contentType.Charset.HasValue && !contentType.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    using var reader = new StreamReader(request.Body, new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
    string input;
    if (request.ContentLength > 65_536) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    try
    {
        var buffer = new char[65_537];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length > 65_536) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        input = new string(buffer, 0, length);
        if (System.Text.Encoding.UTF8.GetByteCount(input) > 65_536) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }
    catch (System.Text.DecoderFallbackException)
    {
        return Results.BadRequest(new { detail = "Use UTF-8 text containing the documented ASCII X12 subset." });
    }
    var results = await intake.ImportAsync(input, DateTime.UtcNow, cancellationToken);
    return results.All(result => result.Status == "Rejected")
        ? Results.UnprocessableEntity(new { interchanges = results })
        : Results.Ok(new { interchanges = results });
}).Accepts<string>("text/plain").Produces(StatusCodes.Status200OK).Produces(StatusCodes.Status422UnprocessableEntity)
  .Produces(StatusCodes.Status415UnsupportedMediaType).Produces(StatusCodes.Status413PayloadTooLarge)
  .WithSummary("Import the synthetic 837P subset and return per-interchange 999-style acknowledgments.");

app.MapGet("/api/queues/correction", async (ClaimsDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Corrections.AsNoTracking().OrderBy(c => c.ClaimId).ToListAsync(ct)));
app.MapGet("/api/outbox", async (ClaimsDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Outbox.AsNoTracking().OrderBy(m => m.ClaimId)
        .Select(m => new { m.Id, m.ClaimId, m.State, m.Deliveries, m.NextAttemptAt, m.LeaseUntil }).ToListAsync(ct)));

app.MapPriorAuthEndpoints();

app.Run();

public sealed record ClaimSubmission(string? Id, string? ProviderNpi, string? MemberId,
    DateOnly ServiceDate, List<string>? ProcedureCodes, decimal Amount,
    string? RuleVersion = ClaimRules.Version, Simulation Simulation = Simulation.None);
public sealed record ClaimResponse(Claim Claim, IReadOnlyList<ClaimEvent> Timeline, IReadOnlyList<ValidationFailure> Validation);
public partial class Program { }
