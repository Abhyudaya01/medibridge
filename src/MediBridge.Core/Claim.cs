namespace MediBridge.Core;

public enum FailureCategory { None, Transient, BusinessRule, Duplicate, RuleVersionConflict, Unclassified }
public enum ClaimStatus { Received, Rejected, RetryPending, Succeeded, Suppressed, ManualReview, PendingProcessing, Processing }
public enum Simulation { None, DownstreamTimeout }

public sealed class Claim
{
    public string Id { get; set; } = "";
    public string ProviderNpi { get; set; } = "";
    public string MemberId { get; set; } = "";
    public DateOnly ServiceDate { get; set; }
    public List<string> ProcedureCodes { get; set; } = [];
    public decimal Amount { get; set; }
    public int AttemptCount { get; set; }
    public int RetryCount => Math.Max(0, AttemptCount - 1);
    public Simulation Simulation { get; set; }
    public ClaimStatus Status { get; set; } = ClaimStatus.Received;
    public FailureCategory FailureCategory { get; set; }
    public string RuleVersion { get; set; } = ClaimRules.Version;
    public List<ValidationFailure> ValidationFailures { get; set; } = [];
}

public sealed class ClaimEvent
{
    public long Id { get; set; }
    public string ClaimId { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Stage { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed record ValidationFailure(string RuleId, string Field, string SuggestedFix);
