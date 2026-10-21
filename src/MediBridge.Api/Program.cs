var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok", version = "0.1.0" }));

app.Run();

// Expose the entry point to WebApplicationFactory integration tests.
public partial class Program { }
