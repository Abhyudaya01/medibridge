using MediBridge.Core;

namespace MediBridge.Infrastructure;

public static class ReliableWrites
{
    public static void Track(ClaimsDbContext db, Claim claim, DateTime timestamp)
    {
        if (claim.Status == ClaimStatus.PendingProcessing)
            db.Outbox.Add(new OutboxMessage { ClaimId = claim.Id, NextAttemptAt = timestamp });
        if (claim.Status == ClaimStatus.Rejected && claim.FailureCategory == FailureCategory.BusinessRule)
        {
            db.Corrections.Add(new CorrectionItem { ClaimId = claim.Id, CreatedAt = timestamp, Reason = System.Text.Json.JsonSerializer.Serialize(claim.ValidationFailures) });
            db.ClaimEvents.Add(new ClaimEvent { ClaimId = claim.Id, Timestamp = timestamp, Stage = "CorrectionQueue", Outcome = "Queued", Detail = "Business-rule failure; automatic retries are prohibited." });
        }
    }
}
