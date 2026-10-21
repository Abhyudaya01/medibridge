using MediBridge.Core;
using MediBridge.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediBridge.Tests;

public class AuditTests
{
    [Fact]
    public async Task ClaimChangesRequireAuditAndEventsCannotBeChangedOrDeleted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteClaimsDbContext>().UseSqlite(connection).Options;
        await using var db = new SqliteClaimsDbContext(options);
        await db.InitializeAsync();
        db.Claims.Add(ClaimRuleTests.Valid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var store = new ClaimStore(db, new ClaimWriteLock());
        await store.SubmitAsync(ClaimRuleTests.Valid(), Simulation.None, new DateTime(2026, 1, 20));
        var count = await db.ClaimEvents.CountAsync();
        var claim = await db.Claims.SingleAsync();
        claim.Status = ClaimStatus.ManualReview;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var audit = await db.ClaimEvents.FirstAsync();
        audit.Detail = "Changed";
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        db.ChangeTracker.Clear();
        audit = await db.ClaimEvents.FirstAsync();
        db.ClaimEvents.Remove(audit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE ClaimEvents SET Detail = 'Changed'"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM ClaimEvents"));
        Assert.Equal(count, await db.ClaimEvents.CountAsync());
        Assert.Equal(ClaimStatus.PendingProcessing, (await db.Claims.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task AuditedStateChangeCommitsAtomicallyAndClaimDeletionIsBlocked()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteClaimsDbContext>().UseSqlite(connection).Options;
        await using var db = new SqliteClaimsDbContext(options);
        await db.InitializeAsync();
        var store = new ClaimStore(db, new ClaimWriteLock());
        await store.SubmitAsync(ClaimRuleTests.Valid(), Simulation.None, new DateTime(2026, 1, 20));
        var claim = await db.Claims.SingleAsync();
        claim.Status = ClaimStatus.ManualReview;
        db.ClaimEvents.Add(new ClaimEvent { ClaimId = claim.Id, Timestamp = new(2026, 1, 21), Stage = "Review", Outcome = "ManualReview", Detail = "Synthetic review opened." });
        await db.SaveChangesAsync();
        Assert.Equal("ManualReview", (await db.ClaimEvents.OrderBy(e => e.Id).LastAsync()).Outcome);
        db.Claims.Remove(claim);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void SqlServerMappingIncludesDuplicateConstraintAndPortableSchema()
    {
        var options = new DbContextOptionsBuilder<SqlClaimsDbContext>()
            .UseSqlServer("Server=localhost;Database=SyntheticSchemaOnly;Integrated Security=true;TrustServerCertificate=true").Options;
        using var db = new SqlClaimsDbContext(options);
        var script = db.Database.GenerateCreateScript();
        Assert.Contains("CREATE TABLE [Claims]", script);
        Assert.Contains("CREATE TABLE [ClaimEvents]", script);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Claims_DuplicateKey]", script);
    }
}
