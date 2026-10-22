using System.Text.Json;
using MediBridge.Infrastructure.Monitoring;
using MediBridge.Infrastructure.Rules;
using MediBridge.Infrastructure.PriorAuth;
using MediBridge.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MediBridge.Infrastructure;

public class ClaimsDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<EndpointObservation> EndpointObservations => Set<EndpointObservation>();
    public DbSet<RuleSetRecord> RuleSets => Set<RuleSetRecord>();
    public DbSet<RuleControl> RuleControls => Set<RuleControl>();
    public DbSet<RuleActivation> RuleActivations => Set<RuleActivation>();
    public DbSet<RuleEvaluationRecord> RuleEvaluations => Set<RuleEvaluationRecord>();
    public DbSet<PriorAuthRecord> PriorAuthRequests => Set<PriorAuthRecord>();
    public DbSet<PriorAuthEvent> PriorAuthEvents => Set<PriorAuthEvent>();
    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<ClaimEvent> ClaimEvents => Set<ClaimEvent>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<HandlerReceipt> Receipts => Set<HandlerReceipt>();
    public DbSet<CorrectionItem> Corrections => Set<CorrectionItem>();
    public DbSet<IdempotencyRecord> Idempotency => Set<IdempotencyRecord>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var endpoint = model.Entity<EndpointObservation>(); endpoint.HasKey(e => e.Id);
        endpoint.Property(e => e.Id).HasMaxLength(32); endpoint.Property(e => e.Method).HasMaxLength(16); endpoint.Property(e => e.Route).HasMaxLength(160);
        endpoint.HasIndex(e => new { e.CompletedAtTicks, e.Method, e.Route });
        if (Database.IsSqlServer()) endpoint.ToTable("EndpointObservations", t => t.UseSqlOutputClause(false));
        model.Entity<RuleEvaluationRecord>().HasIndex(e => new { e.ReceivedAtTicks, e.Id });
        var rules = model.Entity<RuleSetRecord>(); rules.HasKey(r => r.Version); rules.Property(r => r.Version).HasMaxLength(32).UseCollation(Database.IsSqlServer() ? "Latin1_General_100_BIN2" : "BINARY"); rules.Property(r => r.ContentHash).HasMaxLength(64);
        var control = model.Entity<RuleControl>(); control.HasKey(r => r.Id); control.Property(r => r.Revision).IsConcurrencyToken(); control.Property(r => r.ShadowVersion).HasMaxLength(32).UseCollation(Database.IsSqlServer() ? "Latin1_General_100_BIN2" : "BINARY");
        control.HasOne<RuleSetRecord>().WithMany().HasForeignKey(r => r.ShadowVersion).OnDelete(DeleteBehavior.Restrict);
        var activation = model.Entity<RuleActivation>(); activation.HasKey(r => r.Id); activation.Property(r => r.Version).HasMaxLength(32).UseCollation(Database.IsSqlServer() ? "Latin1_General_100_BIN2" : "BINARY"); activation.HasOne<RuleSetRecord>().WithMany().HasForeignKey(r => r.Version).OnDelete(DeleteBehavior.Restrict);
        var evaluation = model.Entity<RuleEvaluationRecord>(); evaluation.HasKey(r => r.Id); evaluation.Property(r => r.Id).HasMaxLength(32); evaluation.Property(r => r.TransactionId).HasMaxLength(64); evaluation.Property(r => r.Domain).HasMaxLength(16); evaluation.Property(r => r.Source).HasMaxLength(32); evaluation.Property(r => r.Provider).HasMaxLength(200); evaluation.Property(r => r.PlanType).HasMaxLength(32);
        foreach (var name in new[] { "LiveVersion", "CandidateVersion" }) evaluation.Property<string>(name).HasMaxLength(32).UseCollation(Database.IsSqlServer() ? "Latin1_General_100_BIN2" : "BINARY");
        evaluation.Property(r => r.LiveHash).HasMaxLength(64); evaluation.Property(r => r.CandidateHash).HasMaxLength(64); evaluation.Property(r => r.LiveOutcome).HasMaxLength(16); evaluation.Property(r => r.CandidateOutcome).HasMaxLength(16);
        evaluation.HasIndex(r => new { r.CandidateVersion, r.ReceivedAtTicks });
        if (Database.IsSqlServer()) { rules.ToTable("RuleSets", t => t.UseSqlOutputClause(false)); activation.ToTable("RuleActivations", t => t.UseSqlOutputClause(false)); evaluation.ToTable("RuleEvaluations", t => t.UseSqlOutputClause(false)); }
        var pa = model.Entity<PriorAuthRecord>(); pa.HasKey(r => r.Id); pa.Property(r => r.Id).HasMaxLength(64).UseCollation(Database.IsSqlServer() ? "Latin1_General_100_BIN2" : "BINARY");
        pa.Property(r => r.Outcome).HasMaxLength(32); pa.Property(r => r.SourceFormat).HasMaxLength(32);
        pa.Property(r => r.Version).IsConcurrencyToken();
        pa.HasIndex(r => new { r.Outcome, r.NextSlaCheckTicks });
        var paEvent = model.Entity<PriorAuthEvent>(); paEvent.HasKey(e => e.Id); paEvent.Property(e => e.RequestId).HasMaxLength(64).UseCollation(Database.IsSqlServer() ? "Latin1_General_100_BIN2" : "BINARY");
        paEvent.Property(e => e.Stage).HasMaxLength(64); paEvent.Property(e => e.Outcome).HasMaxLength(64);
        paEvent.HasIndex(e => new { e.RequestId, e.Id });
        paEvent.HasOne<PriorAuthRecord>().WithMany().HasForeignKey(e => e.RequestId).OnDelete(DeleteBehavior.Restrict);
        if (Database.IsSqlServer()) paEvent.ToTable("PriorAuthEvents", table => table.UseSqlOutputClause(false));
        var claim = model.Entity<Claim>();
        claim.Ignore(c => c.RetryCount);
        claim.HasKey(c => c.Id);
        claim.Property(c => c.Id).HasMaxLength(64);
        claim.Property(c => c.ProviderNpi).HasMaxLength(200);
        claim.Property(c => c.MemberId).HasMaxLength(200);
        claim.Property(c => c.RuleVersion).HasMaxLength(200);
        claim.Property(c => c.PlanType).HasMaxLength(32).HasDefaultValue("Unspecified");
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

        claim.Property(c => c.Simulation).HasConversion<string>().HasMaxLength(32);
        var outbox = model.Entity<OutboxMessage>();
        outbox.HasKey(m => m.Id); outbox.Property(m => m.Id).HasMaxLength(32);
        outbox.Property(m => m.ClaimId).HasMaxLength(64); outbox.HasIndex(m => m.ClaimId).IsUnique();
        outbox.HasOne<Claim>().WithMany().HasForeignKey(m => m.ClaimId).OnDelete(DeleteBehavior.Restrict);
        outbox.Property(m => m.State).HasMaxLength(32); outbox.Property(m => m.LeaseOwner).HasMaxLength(32);
        outbox.Property(m => m.Version).IsConcurrencyToken();
        outbox.Property(m => m.NextAttemptAt).HasConversion(v => v.Ticks, v => new DateTime(v, DateTimeKind.Utc));
        outbox.Property(m => m.LeaseUntil).HasConversion(v => v.HasValue ? (long?)v.Value.Ticks : null, v => v.HasValue ? new DateTime(v.Value, DateTimeKind.Utc) : null);
        outbox.HasIndex(m => new { m.State, m.NextAttemptAt });
        model.Entity<HandlerReceipt>().HasKey(r => r.Id);
        model.Entity<HandlerReceipt>().Property(r => r.Id).HasMaxLength(32);
        model.Entity<HandlerReceipt>().HasOne<OutboxMessage>().WithMany().HasForeignKey(r => r.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CorrectionItem>().HasKey(c => c.ClaimId);
        model.Entity<CorrectionItem>().Property(c => c.ClaimId).HasMaxLength(64);
        model.Entity<CorrectionItem>().HasOne<Claim>().WithMany().HasForeignKey(c => c.ClaimId).OnDelete(DeleteBehavior.Restrict);
        var idem = model.Entity<IdempotencyRecord>(); idem.HasKey(r => r.Id);
        idem.Property(r => r.Id).HasMaxLength(64); idem.Property(r => r.Scope).HasMaxLength(64);
        idem.Property(r => r.Key).HasMaxLength(128).UseCollation(Database.IsSqlServer() ? "Latin1_General_100_BIN2" : "BINARY");
        idem.Property(r => r.Fingerprint).HasMaxLength(64); idem.HasIndex(r => new { r.Scope, r.Key }).IsUnique();
        var general = model.Entity<AuditEntry>(); general.HasKey(a => a.Id);
        general.Property(a => a.EntityType).HasMaxLength(64); general.Property(a => a.EntityId).HasMaxLength(128);
        general.Property(a => a.Action).HasMaxLength(32);
        if (Database.IsSqlServer()) general.ToTable("Audit", table => table.UseSqlOutputClause(false));
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
        if (ChangeTracker.Entries().Any(e => e.Entity is EndpointObservation or RuleSetRecord or RuleActivation or RuleEvaluationRecord && e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Rule definitions, activations and evaluations are append-only.");
        if (ChangeTracker.Entries<RuleControl>().Any(e => e.State == EntityState.Deleted)) throw new InvalidOperationException("Rule control cannot be deleted.");
        if (ChangeTracker.Entries<PriorAuthEvent>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("PriorAuthEvents is append-only.");
        var auditedPaIds = ChangeTracker.Entries<PriorAuthEvent>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.RequestId).ToHashSet(StringComparer.Ordinal);
        if (ChangeTracker.Entries<PriorAuthRecord>().Any(e => e.State == EntityState.Deleted || (e.State is EntityState.Added or EntityState.Modified) && !auditedPaIds.Contains(e.Entity.Id)))
            throw new InvalidOperationException("Prior authorization records require appended timeline events and cannot be deleted.");
        if (ChangeTracker.Entries<AuditEntry>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit is append-only.");
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray())
        {
            if (entry.Entity is PriorAuthRecord paRecord && entry.State == EntityState.Modified) paRecord.Version = (long)entry.Property("Version").OriginalValue! + 1;
            if (entry.Entity is OutboxMessage message && entry.State == EntityState.Modified) message.Version = (long)entry.Property("Version").OriginalValue! + 1;
            if (entry.Entity is HandlerReceipt or IdempotencyRecord && entry.State == EntityState.Modified)
                throw new InvalidOperationException("Committed responses and handler receipts are immutable.");
            if (entry.Entity is not (OutboxMessage or HandlerReceipt or CorrectionItem or IdempotencyRecord or PriorAuthRecord or RuleSetRecord or RuleControl or RuleActivation or RuleEvaluationRecord or EndpointObservation)) continue;
            if (entry.State == EntityState.Deleted) throw new InvalidOperationException("Reliability records cannot be deleted.");
            Audit.Add(new AuditEntry
            {
                Timestamp = DateTime.UtcNow, EntityType = entry.Entity.GetType().Name,
                EntityId = entry.Entity switch { OutboxMessage m => m.Id, HandlerReceipt r => r.Id, CorrectionItem c => c.ClaimId, IdempotencyRecord r => r.Id, PriorAuthRecord p => p.Id, RuleSetRecord r => r.Version, RuleControl => "registry", RuleActivation r => r.Version, RuleEvaluationRecord r => r.Id, EndpointObservation e => e.Id, _ => "" },
                Action = entry.State.ToString(), Detail = entry.Entity is OutboxMessage m2 ? $"{m2.State}; deliveries={m2.Deliveries}; version={m2.Version}" : entry.Entity is RuleControl rc ? $"revision={rc.Revision}; shadow={rc.ShadowVersion ?? "off"}" : entry.Entity is RuleActivation ra ? $"Promoted {ra.Version}; atTicks={ra.ActivatedAtTicks}" : "Durable record saved."
            });
        }
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

    public Task InitializeAsync(CancellationToken cancellationToken = default) => DatabaseBootstrapper.InitializeAsync(this, cancellationToken);
}
