using MediBridge.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MediBridge.Tests;

public class MigrationTests
{
    [Fact]
    public async Task LegacyBaselineUpgradePreservesClaimAndAuditHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"legacy-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<SqliteClaimsDbContext>().UseSqlite($"Data Source={path}").Options;
        await using var db = new SqliteClaimsDbContext(options);
        var baseline = db.Database.GetMigrations().First();
        await db.GetService<IMigrator>().MigrateAsync(baseline);
        // Mimic the pre-migrations schema by removing only the history metadata.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE __EFMigrationsHistory");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Claims (Id, ProviderNpi, MemberId, ServiceDate, ProcedureCodes, Amount, Status, FailureCategory, RuleVersion, ValidationFailures) VALUES ('CLM-LEGACY', '0000000006', 'SYN-LEGACY', '2026-01-20', '[\"SYN-OLD\"]', '42.5', 'Succeeded', 'None', 'phase0-v1', '[]')");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO ClaimEvents (ClaimId, Timestamp, Stage, Outcome, Detail) VALUES ('CLM-LEGACY', '2026-01-20 12:00:00', 'Intake', 'Succeeded', 'Original synthetic history')");
        await db.InitializeAsync(); await db.InitializeAsync();
        Assert.Equal("CLM-LEGACY", (await db.Claims.SingleAsync()).Id);
        Assert.Equal("Original synthetic history", (await db.ClaimEvents.SingleAsync()).Detail);
        Assert.Equal(6, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.True(File.Exists(path + ".legacy-backup.db"));
    }
    [Fact]
    public async Task PriorAuthUpgradePreservesOriginalEventsAndEnrollsOriginalReceiptOnlyOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pa-upgrade-{Guid.NewGuid():N}.db");
        await using var db = new SqliteClaimsDbContext(new DbContextOptionsBuilder<SqliteClaimsDbContext>().UseSqlite($"Data Source={path}").Options);
        await db.GetService<IMigrator>().MigrateAsync(db.Database.GetMigrations().ElementAt(2));
        var receipt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var pair = MediBridge.Parity.PairedRequests.Generate(1)[0];
        var canonical = System.Text.Json.JsonSerializer.Serialize(pair.Expected, MediBridge.Infrastructure.PriorAuth.PriorAuthJson.Options);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO PriorAuthRequests (Id, CanonicalJson, Outcome, SourceFormat, ValidationJson) VALUES ({pair.Expected.Id}, {canonical}, 'Accepted', 'FHIR-R4', '[]')");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO PriorAuthEvents (RequestId, Timestamp, Stage, Outcome, Detail) VALUES ({pair.Expected.Id}, {receipt}, 'Intake', 'Received', 'Original receipt preserved')");
        await db.InitializeAsync();
        var clock = new ManualClock { UtcNow = receipt.AddDays(10) };
        var store = new MediBridge.Infrastructure.PriorAuth.PriorAuthLifecycleStore(db, clock);
        Assert.Equal(1, await store.SweepAsync()); Assert.Equal(0, await store.SweepAsync());
        var result = await new MediBridge.Infrastructure.PriorAuth.PriorAuthStore(db).GetAsync(pair.Expected.Id);
        Assert.Equal(receipt, result!.Lifecycle!.ReceivedAt);
        Assert.Equal(MediBridge.Core.PriorAuth.PriorAuthSla.Breached, result.Lifecycle.Sla);
        Assert.Equal("Original receipt preserved", result.Timeline[0].Detail);
        Assert.Equal(receipt, result.Timeline[0].Timestamp);
        Assert.Equal(1, result.Timeline.Count(e => e.Stage == "SlaClock"));
        Assert.Equal(2, result.Timeline.Count(e => e.Stage == "Escalation"));
        Assert.All(result.Timeline.Skip(1), e => Assert.Equal(clock.UtcNow, e.Timestamp));
        await db.InitializeAsync(); Assert.Equal(0, await store.SweepAsync());
        Assert.Equal(6, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }
    [Fact]
    public async Task UnrecognizedSchemaIsNotBlindlyBaselined()
    {
        var path = Path.Combine(Path.GetTempPath(), $"unknown-{Guid.NewGuid():N}.db");
        await using var db = new SqliteClaimsDbContext(new DbContextOptionsBuilder<SqliteClaimsDbContext>().UseSqlite($"Data Source={path}").Options);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE Claims (Id TEXT PRIMARY KEY)");
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.InitializeAsync());
    }
}
