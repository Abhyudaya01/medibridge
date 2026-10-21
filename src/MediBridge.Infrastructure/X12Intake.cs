using System.Text.Json;
using MediBridge.Core;
using MediBridge.Core.X12;
using Microsoft.EntityFrameworkCore;

namespace MediBridge.Infrastructure;

public sealed record X12ClaimOutcome(string Id, string Status, Claim? Claim, IReadOnlyList<ValidationFailure> Validation);
public sealed record X12IntakeResult(string? InterchangeControlNumber, string Status, X12Acknowledgment Acknowledgment,
    IReadOnlyList<X12ParsedClaim> ParsedClaims, IReadOnlyList<X12ClaimOutcome> Outcomes);

public sealed class X12Intake(ClaimsDbContext db, ClaimWriteLock writeLock)
{
    public async Task<IReadOnlyList<X12IntakeResult>> ImportAsync(string input, DateTime timestamp, CancellationToken cancellationToken = default)
    {
        List<X12IntakeResult> results = [];
        foreach (var interchange in X12Parser.Parse(input))
        {
            var acknowledgment = X12Acknowledgments.Generate(interchange);
            if (!interchange.Accepted)
            {
                results.Add(new(interchange.ControlNumber, "Rejected", acknowledgment, interchange.Claims, []));
                continue;
            }
            await writeLock.Gate.WaitAsync(cancellationToken);
            try
            {
                // A collision with another process can occur after our read. Roll back the whole
                // interchange and re-evaluate once against the newly committed database state.
                for (var attempt = 0; ; attempt++)
                {
                    db.ChangeTracker.Clear();
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                    try
                    {
                        List<X12ClaimOutcome> outcomes = [];
                        var seenIds = new HashSet<string>(StringComparer.Ordinal);
                        var reserved = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var parsed in interchange.Claims)
                        {
                            var claim = parsed.Claim;
                            if (!seenIds.Add(claim.Id) || await db.Claims.AnyAsync(c => c.Id == claim.Id, cancellationToken))
                            {
                                outcomes.Add(new(claim.Id, "DuplicateId", null, [new("DUPLICATE_ID", "Id", "Fetch the existing claim or use a new synthetic claim ID; its history is unchanged.")]));
                                continue;
                            }
                            var key = ClaimRules.DuplicateKey(claim);
                            var duplicate = reserved.Contains(key) || await db.Claims.AnyAsync(c => EF.Property<string?>(c, "DuplicateKey") == key, cancellationToken);
                            var result = ClaimProcessor.Process(claim, duplicate, Simulation.None, timestamp);
                            db.Claims.Add(claim);
                            if (!duplicate) { db.Entry(claim).Property("DuplicateKey").CurrentValue = key; reserved.Add(key); }
                            // Preserve HI metadata and source controls without storing the raw interchange.
                            db.ClaimEvents.Add(new ClaimEvent
                            {
                                ClaimId = claim.Id, Timestamp = timestamp, Stage = "X12Intake", Outcome = "Accepted",
                                Detail = JsonSerializer.Serialize(new { interchange.ControlNumber, parsed.GroupControlNumber, parsed.TransactionControlNumber, parsed.SegmentPosition, parsed.DiagnosisCodes })
                            });
                            db.ClaimEvents.AddRange(result.Timeline);
                            outcomes.Add(new(claim.Id, claim.Status.ToString(), claim, claim.ValidationFailures));
                        }
                        await db.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        results.Add(new(interchange.ControlNumber, "Accepted", acknowledgment, interchange.Claims, outcomes));
                        break;
                    }
                    catch (DbUpdateException exception) when (attempt == 0 && ClaimStore.IsUniqueViolation(exception))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }
                }
            }
            finally { db.ChangeTracker.Clear(); writeLock.Gate.Release(); }
        }
        return results;
    }
}
