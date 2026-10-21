using MediBridge.Core;
using MediBridge.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace MediBridge.Tests;

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEDIBRIDGE_TEST_SQL"))) Skip = "SQL Server integration runs in the CI container."; }
}
public sealed class SqlApiFactory : ClaimsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ConnectionStrings:Sql", Environment.GetEnvironmentVariable("MEDIBRIDGE_TEST_SQL"));
    }
}
public class SqlIntegrationTests
{
    [SqlFact]
    public async Task SqlMigrationsIdempotencyOutboxAndConcurrencyWorkAgainstRealServer()
    {
        await using var factory = new SqlApiFactory(); using var client = factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var id = "CLM-SQL-" + Guid.NewGuid().ToString("N");
        var request = new ClaimSubmission(id, "0000000006", "SYN-SQL-" + Guid.NewGuid().ToString("N"), new(2026, 1, 20), ["SYN-SQL"], 20m);
        var response = await client.PostAsJsonAsync("/api/claims", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var replay = await client.PostAsJsonAsync("/api/claims", request);
        Assert.Equal(await response.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
            Assert.True(db.Database.IsSqlServer()); Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
        }
        var dispatcher = factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 8; i++) await Task.WhenAll(dispatcher.DispatchOneAsync(), dispatcher.DispatchOneAsync());
        using var check = factory.Services.CreateScope(); var saved = check.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        Assert.Equal(ClaimStatus.Succeeded, (await saved.Claims.SingleAsync(c => c.Id == id)).Status);
        Assert.Equal(1, await saved.Receipts.CountAsync(r => saved.Outbox.Any(m => m.Id == r.Id && m.ClaimId == id)));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => saved.Database.ExecuteSqlRawAsync("UPDATE ClaimEvents SET Detail='Forbidden'"));
    }
}
