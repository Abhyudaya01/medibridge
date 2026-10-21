using System.Text.Json;
using MediBridge.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MediBridge.Infrastructure;

public sealed class ClaimsDbContext(DbContextOptions<ClaimsDbContext> options) : DbContext(options)
{
    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<ClaimEvent> ClaimEvents => Set<ClaimEvent>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var claim = model.Entity<Claim>();
        claim.HasKey(c => c.Id);
        claim.Property(c => c.Id).HasMaxLength(64);
        claim.Property(c => c.ProviderNpi).HasMaxLength(200);
        claim.Property(c => c.MemberId).HasMaxLength(200);
        claim.Property(c => c.RuleVersion).HasMaxLength(200);
        // Preserve exact submitted decimals, including rejected currency precision, on both providers.
        claim.Property(c => c.Amount).HasConversion<string>().HasMaxLength(64);
        claim.Property(c => c.Status).HasConversion<string>().HasMaxLength(32);
        claim.Property(c => c.FailureCategory).HasConversion<string>().HasMaxLength(32);
        claim.Property<string?>("DuplicateKey").HasMaxLength(64);
        claim.HasIndex("DuplicateKey").IsUnique().HasFilter("[DuplicateKey] IS NOT NULL");
        claim.Property(c => c.ProcedureCodes).HasConversion(
            value => Serialize(value), value => Deserialize<List<string>>(value))
            .Metadata.SetValueComparer(JsonComparer<List<string>>());
        claim.Property(c => c.ValidationFailures).HasConversion(
            value => Serialize(value), value => Deserialize<List<ValidationFailure>>(value))
            .Metadata.SetValueComparer(JsonComparer<List<ValidationFailure>>());

        var audit = model.Entity<ClaimEvent>();
        if (Database.IsSqlServer()) audit.ToTable("ClaimEvents", table => table.UseSqlOutputClause(false));
        audit.HasKey(e => e.Id);
        audit.Property(e => e.ClaimId).HasMaxLength(64);
        audit.Property(e => e.Stage).HasMaxLength(64);
        audit.Property(e => e.Outcome).HasMaxLength(64);
        audit.HasIndex(e => new { e.ClaimId, e.Id });
        audit.HasOne<Claim>().WithMany().HasForeignKey(e => e.ClaimId).OnDelete(DeleteBehavior.Restrict);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value)!;
    private static ValueComparer<T> JsonComparer<T>() => new(
        (left, right) => Serialize(left) == Serialize(right),
        value => Serialize(value).GetHashCode(), value => Deserialize<T>(Serialize(value)));

    private void EnforceAudit()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<ClaimEvent>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("ClaimEvents is append-only.");
        if (ChangeTracker.Entries<Claim>().Any(e => e.State == EntityState.Deleted))
            throw new InvalidOperationException("Claims cannot be deleted; preserve their audit history.");
        var auditedIds = ChangeTracker.Entries<ClaimEvent>().Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity.ClaimId).ToHashSet(StringComparer.Ordinal);
        if (ChangeTracker.Entries<Claim>().Any(e => (e.State is EntityState.Added or EntityState.Modified) && !auditedIds.Contains(e.Entity.Id)))
            throw new InvalidOperationException("Every claim state change requires an appended audit event.");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Database.EnsureCreatedAsync(cancellationToken);
        // Defense in depth: direct SQL cannot mutate or delete persisted audit events.
        if (Database.IsSqlite())
        {
            await Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER IF NOT EXISTS ClaimEvents_NoUpdate BEFORE UPDATE ON ClaimEvents
                BEGIN SELECT RAISE(ABORT, 'ClaimEvents is append-only'); END;
                """, cancellationToken);
            await Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER IF NOT EXISTS ClaimEvents_NoDelete BEFORE DELETE ON ClaimEvents
                BEGIN SELECT RAISE(ABORT, 'ClaimEvents is append-only'); END;
                """, cancellationToken);
        }
        else if (Database.IsSqlServer())
        {
            await Database.ExecuteSqlRawAsync("""
                CREATE OR ALTER TRIGGER ClaimEvents_AppendOnly ON ClaimEvents
                INSTEAD OF UPDATE, DELETE AS
                BEGIN THROW 51000, 'ClaimEvents is append-only', 1; END;
                """, cancellationToken);
        }
    }
}
