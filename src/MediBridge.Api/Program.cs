using System.Text.Json.Serialization;
using MediBridge.Core;
using MediBridge.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
{
    Title = "MediBridge synthetic claims API", Version = "0.2.0",
    Description = "Synthetic data only. Not affiliated with any payer. No real member or provider data."
}));
builder.Services.AddSingleton<ClaimWriteLock>();
builder.Services.AddScoped<ClaimStore>();
builder.Services.AddDbContext<ClaimsDbContext>(options =>
{
    var sql = builder.Configuration.GetConnectionString("Sql");
    if (!string.IsNullOrWhiteSpace(sql)) options.UseSqlServer(sql);
    else
    {
        var onAppService = OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
        var path = builder.Configuration["Database:Path"]
            ?? (onAppService ? "/home/data/medibridge.db" : Path.Combine(builder.Environment.ContentRootPath, "Data", "medibridge.db"));
        path = Path.GetFullPath(path, builder.Environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString());
    }
});

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ClaimsDbContext>().InitializeAsync();
    await SyntheticSeed.RunAsync(scope.ServiceProvider.GetRequiredService<ClaimStore>());
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/health", () => Results.Ok(new { status = "ok", version = "0.2.0" }));
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
        _ => Results.Created($"/api/claims/{Uri.EscapeDataString(claim.Id)}", response)
    };
});

app.Run();

public sealed record ClaimSubmission(string? Id, string? ProviderNpi, string? MemberId,
    DateOnly ServiceDate, List<string>? ProcedureCodes, decimal Amount,
    string? RuleVersion = ClaimRules.Version, Simulation Simulation = Simulation.None);
public sealed record ClaimResponse(Claim Claim, IReadOnlyList<ClaimEvent> Timeline, IReadOnlyList<ValidationFailure> Validation);
public partial class Program { }
