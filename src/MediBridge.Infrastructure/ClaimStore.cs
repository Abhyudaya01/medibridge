using MediBridge.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediBridge.Infrastructure;

public sealed class ClaimWriteLock
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

public sealed class ClaimStore(ClaimsDbContext db, ClaimWriteLock writeLock)
{
    public async Task<ProcessingResult?> SubmitAsync(Claim claim, Simulation simulation, DateTime timestamp, CancellationToken cancellationToken = default)
    {
        await writeLock.Gate.WaitAsync(cancellationToken);
        try
        {
            if (await db.Claims.AnyAsync(c => c.Id == claim.Id, cancellationToken)) return null;
            var valid = ClaimRules.Validate(claim).Count == 0;
            var key = valid ? ClaimRules.DuplicateKey(claim) : null;
            var duplicate = key is not null && await HasKeyAsync(key, cancellationToken);
            var result = ClaimProcessor.Process(claim, duplicate, simulation, timestamp);
            Track(result, key);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // The database's unique index also arbitrates writes from other processes.
                db.ChangeTracker.Clear();
                if (await db.Claims.AnyAsync(c => c.Id == claim.Id, cancellationToken)) return null;
                if (key is null || !await HasKeyAsync(key, cancellationToken)) throw;
                result = ClaimProcessor.Process(claim, true, simulation, timestamp);
                Track(result, null);
                await db.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
        finally { writeLock.Gate.Release(); }
    }

    private Task<bool> HasKeyAsync(string key, CancellationToken cancellationToken) =>
        db.Claims.AnyAsync(c => EF.Property<string?>(c, "DuplicateKey") == key, cancellationToken);

    private void Track(ProcessingResult result, string? key)
    {
        db.Claims.Add(result.Claim);
        if (result.Claim.Status is ClaimStatus.Succeeded or ClaimStatus.ManualReview or ClaimStatus.RetryPending)
            db.Entry(result.Claim).Property("DuplicateKey").CurrentValue = key;
        db.ClaimEvents.AddRange(result.Timeline);
    }

    internal static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException switch
    {
        SqliteException sqlite => sqlite.SqliteExtendedErrorCode is 1555 or 2067,
        SqlException sql => sql.Number is 2601 or 2627,
        _ => false
    };
}
