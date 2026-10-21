namespace MediBridge.Core;

public sealed record ProcessingResult(Claim Claim, IReadOnlyList<ClaimEvent> Timeline);

public static class ClaimProcessor
{
    // All time and external outcomes are inputs, making this simulation deterministic.
    public static ProcessingResult Process(Claim claim, bool duplicate, Simulation simulation, DateTime timestamp)
    {
        List<ClaimEvent> events = [];
        Add("Intake", "Received", "Synthetic claim received.");
        claim.ValidationFailures = ClaimRules.Validate(claim);
        if (claim.ValidationFailures.Count > 0)
        {
            claim.Status = ClaimStatus.Rejected;
            claim.FailureCategory = FailureCategory.BusinessRule;
            Add("Validation", "Rejected", string.Join("; ", claim.ValidationFailures.Select(f => $"{f.RuleId}: {f.SuggestedFix}")));
        }
        else
        {
            Add("Validation", "Passed", $"Required fields and NPI checksum passed under {ClaimRules.Version}.");
            if (duplicate)
            {
                claim.Status = ClaimStatus.Suppressed;
                claim.FailureCategory = FailureCategory.Duplicate;
                claim.ValidationFailures.Add(new("DUPLICATE_CLAIM", "ProcedureCodes", "An existing claim has this provider, member, service date, and code set. Review that claim instead of resubmitting."));
                Add("DuplicateDetection", "Suppressed", "Matching provider, member, service date, and normalized code set already recorded.");
            }
            else if (claim.RuleVersion != ClaimRules.Version)
            {
                claim.Status = ClaimStatus.ManualReview;
                claim.FailureCategory = FailureCategory.RuleVersionConflict;
                claim.ValidationFailures.Add(new("RULE_VERSION_CONFLICT", "RuleVersion", $"Review the claim against {ClaimRules.Version} before resubmitting with a new claim ID."));
                Add("DuplicateDetection", "Passed", "No matching claim found.");
                Add("RuleVersion", "ManualReview", $"Submitted rules differ from active version {ClaimRules.Version}; manual review required.");
            }
            else
            {
                Add("DuplicateDetection", "Passed", "No matching claim found.");
                Add("RuleVersion", "Passed", $"Active version {ClaimRules.Version} confirmed.");
                if (simulation == Simulation.DownstreamTimeout)
                {
                    claim.Status = ClaimStatus.RetryPending;
                    claim.FailureCategory = FailureCategory.Transient;
                    Add("Downstream", "Timeout", "Simulated downstream timeout on attempt 1; no external service was called.");
                    Add("Recovery", "RetryPending", "Transient failure classified; deterministic retry scheduled.");
                    Add("Recovery", "Retrying", "Simulated retry attempt 2 started.");
                }
                claim.Status = ClaimStatus.Succeeded;
                claim.FailureCategory = FailureCategory.None;
                Add("Downstream", "Succeeded", simulation == Simulation.DownstreamTimeout ? "Simulated retry attempt 2 succeeded; transient failure recovered." : "Synthetic downstream processing succeeded.");
            }
        }
        return new(claim, events);

        void Add(string stage, string outcome, string detail) => events.Add(new ClaimEvent
        {
            ClaimId = claim.Id, Timestamp = timestamp.AddSeconds(events.Count), Stage = stage, Outcome = outcome, Detail = detail
        });
    }
}
