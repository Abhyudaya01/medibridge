using System.Text.Json;
using MediBridge.Core;
using MediBridge.Core.Rules;
using MediBridge.Infrastructure.Rules;
using MediBridge.Core.X12;
using Microsoft.EntityFrameworkCore;

namespace MediBridge.Infrastructure;

public sealed record X12ClaimOutcome(string Id, string Status, Claim? Claim, IReadOnlyList<ValidationFailure> Validation);
public sealed record X12IntakeResult(string? InterchangeControlNumber, string Status, X12Acknowledgment Acknowledgment,
    IReadOnlyList<X12ParsedClaim> ParsedClaims, IReadOnlyList<X12ClaimOutcome> Outcomes);

public sealed class X12Intake(ClaimsDbContext db, ClaimWriteLock writeLock)
{
    public async Task<IReadOnlyList<X12IntakeResult>> ImportAsync(string input, DateTime timestamp, CancellationToken cancellationToken = default, string planType = "Unspecified")
    {
        List<X12IntakeResult> results = [];
        var receipt = new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
        var snapshot = await new RuleRegistry(db).SnapshotAsync(receipt, cancellationToken);
        foreach (var parsedInterchange in X12Parser.Parse(input, validateDomain: false))
        {
            var evaluations = new Dictionary<X12ParsedClaim, EvaluatedRules>();
            var errors = parsedInterchange.Errors.ToList();
            foreach (var parsed in parsedInterchange.Claims)
            {
                parsed.Claim.PlanType = planType; parsed.Claim.RuleVersion = snapshot.Live.Version;
                var evaluated = await RuleRegistry.EvaluateAsync(snapshot, RuleInput.From(parsed.Claim), receipt, "X12-837P", parsedInterchange.Accepted, cancellationToken);
                evaluations[parsed] = evaluated;
                foreach (var failure in evaluated.Live.Failures)
                {
                    var location = parsed.Locations?.GetValueOrDefault(failure.Field) ?? new("CLM", parsed.SegmentPosition, 0);
                    var transaction = parsedInterchange.Transactions.FirstOrDefault(t => t.GroupControlNumber == parsed.GroupControlNumber && t.ControlNumber == parsed.TransactionControlNumber);
                    errors.Add(new(location.SegmentId, location.SegmentPosition, location.ElementPosition, $"{failure.RuleId}: {failure.SuggestedFix}",
                        TransactionPosition: transaction is null ? null : location.SegmentPosition - transaction.SegmentPosition + 1,
                        GroupControlNumber: parsed.GroupControlNumber, TransactionControlNumber: parsed.TransactionControlNumber));
                }
            }
            var interchange = parsedInterchange with { Errors = errors };
            var acknowledgment = X12Acknowledgments.Generate(interchange);
            if (!interchange.Accepted)
            {
                db.RuleEvaluations.AddRange(evaluations.Where(e => e.Key.Claim.Id.Length is >= 1 and <= 64).Select(e => e.Value.Record));
                await db.SaveChangesAsync(cancellationToken);
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
                    var ownsTransaction = db.Database.CurrentTransaction is null;
                    await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
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
                            var evaluation = evaluations[parsed];
                            db.RuleEvaluations.Add(evaluation.Record);
                            var result = ClaimProcessor.Process(claim, duplicate, Simulation.None, timestamp, evaluation.Live, snapshot.Live.Version);
                            db.Claims.Add(claim);
                            if (!duplicate) { db.Entry(claim).Property("DuplicateKey").CurrentValue = key; reserved.Add(key); }
                            // Preserve HI metadata and source controls without storing the raw interchange.
                            db.ClaimEvents.Add(new ClaimEvent
                            {
                                ClaimId = claim.Id, Timestamp = timestamp, Stage = "X12Intake", Outcome = "Accepted",
                                Detail = JsonSerializer.Serialize(new { interchange.ControlNumber, parsed.GroupControlNumber, parsed.TransactionControlNumber, parsed.SegmentPosition, parsed.DiagnosisCodes })
                            });
                            db.ClaimEvents.AddRange(result.Timeline);
                            ReliableWrites.Track(db, claim, timestamp);
                            outcomes.Add(new(claim.Id, claim.Status.ToString(), claim, claim.ValidationFailures));
                        }
                        await db.SaveChangesAsync(cancellationToken);
                        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                        results.Add(new(interchange.ControlNumber, "Accepted", acknowledgment, interchange.Claims, outcomes));
                        break;
                    }
                    catch (DbUpdateException exception) when (ownsTransaction && attempt == 0 && ClaimStore.IsUniqueViolation(exception))
                    {
                        await transaction!.RollbackAsync(cancellationToken);
                    }
                }
            }
            finally { db.ChangeTracker.Clear(); writeLock.Gate.Release(); }
        }
        return results;
    }
}
