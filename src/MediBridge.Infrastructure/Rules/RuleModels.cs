namespace MediBridge.Infrastructure.Rules;

public sealed class RuleSetRecord
{
    public string Version { get; set; } = "";
    public string DefinitionJson { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public long EffectiveFromTicks { get; set; }
    public long? EffectiveUntilTicks { get; set; }
    public long CreatedAtTicks { get; set; }
}
public sealed class RuleControl
{
    public int Id { get; set; }
    public string? ShadowVersion { get; set; }
    public long Revision { get; set; }
}
public sealed class RuleActivation
{
    public long Id { get; set; }
    public string Version { get; set; } = "";
    public long ActivatedAtTicks { get; set; }
    public long Revision { get; set; }
}
public sealed class RuleEvaluationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TransactionId { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Source { get; set; } = "";
    public long ReceivedAtTicks { get; set; }
    public string Provider { get; set; } = "";
    public string CodesJson { get; set; } = "[]";
    public string PlanType { get; set; } = "Unspecified";
    public string LiveVersion { get; set; } = "";
    public string LiveHash { get; set; } = "";
    public string LiveJson { get; set; } = "";
    public string LiveOutcome { get; set; } = "";
    public string? CandidateVersion { get; set; }
    public string? CandidateHash { get; set; }
    public string? CandidateJson { get; set; }
    public string? CandidateOutcome { get; set; }
    public bool ProtocolAccepted { get; set; } = true;
}
