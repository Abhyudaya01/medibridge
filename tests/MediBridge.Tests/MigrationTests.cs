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
        Assert.Equal(3, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.True(File.Exists(path + ".legacy-backup.db"));
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
