using MediBridge.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediBridge.Infrastructure;

public sealed class OutboxDispatcher(IServiceScopeFactory scopes, TimeProvider clock, IFaultInjector faults)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    public async Task<bool> DispatchOneAsync(CancellationToken cancellationToken = default)
    {
        string id, owner;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
            var now = Now;
            var message = await db.Outbox.OrderBy(m => m.NextAttemptAt).FirstOrDefaultAsync(m =>
                (m.State == "Pending" && m.NextAttemptAt <= now) || ((m.State == "Processing" || m.State == "AwaitingAck") && m.LeaseUntil <= now), cancellationToken);
            if (message is null) return false;
            if (message.LeaseUntil is not null) Event(db, message.ClaimId, "LeaseExpired", "Unacknowledged delivery recovered after its lease expired.");
            message.State = "Processing"; message.LeaseOwner = owner = Guid.NewGuid().ToString("N");
            message.LeaseUntil = now.AddSeconds(30); message.Deliveries++; id = message.Id;
            Event(db, message.ClaimId, "DeliveryLeased", $"Delivery {message.Deliveries} leased for at-least-once handling.");
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { return true; }
        }
        await faults.CheckpointAsync("AfterLease", cancellationToken);
        int attempt;
        Simulation simulation;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var message = await db.Outbox.SingleAsync(m => m.Id == id, cancellationToken);
            if (!Owns(message, owner)) return true;
            if (await db.Receipts.AnyAsync(r => r.Id == id, cancellationToken))
            {
                message.State = "Completed"; message.LeaseOwner = null; message.LeaseUntil = null;
                Event(db, message.ClaimId, "DuplicateDeliveryIgnored", "Handler receipt already exists; business effect was not repeated.");
                await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return true;
            }
            var claim = await db.Claims.SingleAsync(c => c.Id == message.ClaimId, cancellationToken);
            if (claim.AttemptCount >= RetryPolicy.MaxAttempts)
            {
                Terminal(db, message, claim, FailureCategory.Transient, "Attempt budget exhausted during crash recovery.");
                await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return true;
            }
            message.Version++;
            claim.AttemptCount++; attempt = claim.AttemptCount; simulation = claim.Simulation; claim.Status = ClaimStatus.Processing;
            Event(db, claim.Id, "AttemptStarted", $"Processing attempt {attempt} of {RetryPolicy.MaxAttempts}.");
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        }
        await faults.CheckpointAsync("AfterAttemptStarted", cancellationToken);
        FailureDescriptor? failure;
        try
        {
            failure = await faults.BeforeAttemptAsync(id, attempt, cancellationToken);
            failure ??= simulation == Simulation.DownstreamTimeout && attempt == 1 ? new("Timeout", "Synthetic downstream timeout on attempt 1.") : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { failure = FailureAdapters.Describe(exception); }
        if (failure is not null) { await RecordFailureAsync(id, owner, failure, cancellationToken); return true; }
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var message = await db.Outbox.SingleAsync(m => m.Id == id, cancellationToken);
            if (!Owns(message, owner)) return true;
            var claim = await db.Claims.SingleAsync(c => c.Id == message.ClaimId, cancellationToken);
            claim.Status = ClaimStatus.Succeeded; claim.FailureCategory = FailureCategory.None;
            db.Receipts.Add(new HandlerReceipt { Id = id, CompletedAt = Now });
            message.State = "AwaitingAck";
            Event(db, claim.Id, "AttemptSucceeded", $"Synthetic handler effect committed once on attempt {claim.AttemptCount}.");
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or System.Data.Common.DbException or TimeoutException)
        { await RecordFailureAsync(id, owner, FailureAdapters.Describe(exception), cancellationToken); return true; }
        await faults.CheckpointAsync("AfterHandlerCommit", cancellationToken);
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
            var message = await db.Outbox.SingleAsync(m => m.Id == id, cancellationToken);
            if (Owns(message, owner))
            {
                message.State = "Completed"; message.LeaseOwner = null; message.LeaseUntil = null;
                Event(db, message.ClaimId, "DeliveryCompleted", "Delivery acknowledged after durable handler receipt.");
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        return true;
    }

    private bool Owns(OutboxMessage message, string owner) => message.LeaseOwner == owner && message.LeaseUntil > Now;
    private async Task RecordFailureAsync(string id, string owner, FailureDescriptor failure, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var message = await db.Outbox.SingleAsync(m => m.Id == id, cancellationToken);
        if (!Owns(message, owner)) return;
        if (await db.Receipts.AnyAsync(r => r.Id == id, cancellationToken))
        {
            message.State = "Completed"; message.LeaseOwner = null; message.LeaseUntil = null;
            Event(db, message.ClaimId, "CommitConfirmed", "Handler receipt proves the effect committed despite an uncertain database acknowledgment.");
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return;
        }
        var claim = await db.Claims.SingleAsync(c => c.Id == message.ClaimId, cancellationToken);
        var category = FailureClassifier.Classify(failure);
        var delay = category == FailureCategory.Transient ? RetryPolicy.Delay(claim.AttemptCount, Random.Shared.NextDouble()) : null;
        claim.FailureCategory = category;
        Event(db, claim.Id, "AttemptFailed", $"{category}; attempt {claim.AttemptCount}; kind={failure.Kind}; HTTP={failure.HttpStatus}; SQL={failure.SqlError}; {failure.Detail}");
        if (delay is not null)
        {
            claim.Status = ClaimStatus.RetryPending; message.State = "Pending"; message.NextAttemptAt = Now.Add(delay.Value);
            message.LeaseOwner = null; message.LeaseUntil = null;
            Event(db, claim.Id, "RetryScheduled", $"Attempt {claim.AttemptCount + 1} due at {message.NextAttemptAt:O}.");
        }
        else Terminal(db, message, claim, category, category == FailureCategory.BusinessRule ? "Business rule: never retry." : "Manual review required; automatic processing stopped.");
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }
    private void Terminal(ClaimsDbContext db, OutboxMessage message, Claim claim, FailureCategory category, string detail)
    {
        claim.FailureCategory = category; claim.Status = category == FailureCategory.BusinessRule ? ClaimStatus.Rejected : ClaimStatus.ManualReview;
        message.State = "DeadLetter"; message.LeaseOwner = null; message.LeaseUntil = null;
        if (category == FailureCategory.BusinessRule)
        {
            db.Entry(claim).Property("DuplicateKey").CurrentValue = null;
            db.Corrections.Add(new CorrectionItem { ClaimId = claim.Id, CreatedAt = Now, Reason = detail });
        }
        Event(db, claim.Id, "ProcessingStopped", detail);
    }
    private void Event(ClaimsDbContext db, string claimId, string outcome, string detail) =>
        db.ClaimEvents.Add(new ClaimEvent { ClaimId = claimId, Timestamp = Now, Stage = "Dispatcher", Outcome = outcome, Detail = detail });
}
