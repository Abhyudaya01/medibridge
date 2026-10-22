namespace MediBridge.Core.PriorAuth;

public interface IClock { DateTimeOffset UtcNow { get; } }
public enum PriorAuthDecision { Open, Pended, Approved, Denied }
public enum PriorAuthSla { OnTrack, AtRisk, Breached }
public enum DenialReason { MissingDocumentation, ServiceNotSupported, DuplicateRequest, SyntheticPolicyNotMet }
public sealed record DecisionCommand(string? Decision, string? ReasonCode = null);
public sealed record DecisionError(string Code, string Field, string Reason);

public sealed record PriorAuthLifecycle(PriorAuthPriority Priority, DateTimeOffset ReceivedAt,
    PriorAuthDecision Decision = PriorAuthDecision.Open, PriorAuthSla Sla = PriorAuthSla.OnTrack,
    DateTimeOffset? DecidedAt = null, DenialReason? ReasonCode = null)
{
    public TimeSpan Window => Priority == PriorAuthPriority.Expedited ? TimeSpan.FromHours(72) : TimeSpan.FromDays(7);
    public DateTimeOffset DueAt => ReceivedAt + Window;
    public DateTimeOffset AtRiskAt => ReceivedAt + TimeSpan.FromTicks(Window.Ticks * 4 / 5);
    public bool IsFinal => Decision is PriorAuthDecision.Approved or PriorAuthDecision.Denied;
    public DateTimeOffset? NextCheckAt => IsFinal || Sla == PriorAuthSla.Breached ? null : Sla == PriorAuthSla.OnTrack ? AtRiskAt : DueAt;
}

public static class PriorAuthLifecycleRules
{
    // Both thresholds are inclusive. A final decision at the deadline is a missed deadline.
    public static IReadOnlyList<PriorAuthLifecycle> Escalations(PriorAuthLifecycle state, DateTimeOffset now)
    {
        List<PriorAuthLifecycle> transitions = [];
        if (state.IsFinal) return transitions;
        if (state.Sla == PriorAuthSla.OnTrack && now >= state.AtRiskAt)
        { state = state with { Sla = PriorAuthSla.AtRisk }; transitions.Add(state); }
        if (state.Sla == PriorAuthSla.AtRisk && now >= state.DueAt)
            transitions.Add(state with { Sla = PriorAuthSla.Breached });
        return transitions;
    }

    public static DecisionError? Validate(PriorAuthLifecycle state, DecisionCommand command, DateTimeOffset now)
    {
        if (state.IsFinal) return new("PA_FINAL", "Decision", "A final decision cannot be changed.");
        if (now < state.ReceivedAt) return new("PA_CLOCK", "Decision", "Decision time must not precede receipt.");
        if (!Enum.TryParse<PriorAuthDecision>(command.Decision, out var decision) || !Enum.IsDefined(decision)
            || command.Decision != decision.ToString() || decision == PriorAuthDecision.Open)
            return new("PA_DECISION", "Decision", "Use Approved, Denied or Pended.");
        if (decision == PriorAuthDecision.Pended && state.Decision == PriorAuthDecision.Pended)
            return new("PA_TRANSITION", "Decision", "This request is already pended; submit a final decision.");
        if (decision == PriorAuthDecision.Denied)
        {
            if (!Enum.TryParse<DenialReason>(command.ReasonCode, out var reason) || !Enum.IsDefined(reason) || command.ReasonCode != reason.ToString())
                return new("PA_DENIAL_REASON", "ReasonCode", "Use MissingDocumentation, ServiceNotSupported, DuplicateRequest or SyntheticPolicyNotMet.");
        }
        else if (command.ReasonCode is not null)
            return new("PA_REASON_NOT_ALLOWED", "ReasonCode", "A reason code applies only to Denied.");
        return null;
    }

    public static PriorAuthLifecycle Decide(PriorAuthLifecycle state, DecisionCommand command, DateTimeOffset now)
    {
        if (Validate(state, command, now) is { } error) throw new ArgumentException(error.Reason);
        var decision = Enum.Parse<PriorAuthDecision>(command.Decision!);
        return state with { Decision = decision, DecidedAt = decision == PriorAuthDecision.Pended ? null : now,
            ReasonCode = decision == PriorAuthDecision.Denied ? Enum.Parse<DenialReason>(command.ReasonCode!) : null };
    }
}

public sealed record PriorAuthMetricObservation(string RequestId, PriorAuthLifecycle State);
public sealed record PriorAuthMetrics(string Priority, int Received, int Open, int Pended, int Approved, int Denied,
    int Finalized, double? ApprovalRate, double? DenialRate, double? MeanTurnaroundHours, double? MedianTurnaroundHours);
public static class PriorAuthMetricMath
{
    public static IReadOnlyList<PriorAuthMetrics> Compute(IEnumerable<PriorAuthMetricObservation> audit)
    {
        var states = audit.GroupBy(e => e.RequestId, StringComparer.Ordinal).Select(g => g.Last().State).ToArray();
        return new[] { Summarize("All", states), Summarize("Standard", states.Where(s => s.Priority == PriorAuthPriority.Standard)),
            Summarize("Expedited", states.Where(s => s.Priority == PriorAuthPriority.Expedited)) };
    }
    private static PriorAuthMetrics Summarize(string priority, IEnumerable<PriorAuthLifecycle> source)
    {
        var states = source.ToArray(); var final = states.Where(s => s.IsFinal).ToArray();
        var times = final.Select(s => (s.DecidedAt!.Value - s.ReceivedAt).TotalHours).Order().ToArray();
        var approved = final.Count(s => s.Decision == PriorAuthDecision.Approved); var denied = final.Length - approved;
        return new(priority, states.Length, states.Count(s => s.Decision == PriorAuthDecision.Open), states.Count(s => s.Decision == PriorAuthDecision.Pended),
            approved, denied, final.Length, final.Length == 0 ? null : (double)approved / final.Length,
            final.Length == 0 ? null : (double)denied / final.Length, times.Length == 0 ? null : times.Average(),
            times.Length == 0 ? null : (times[(times.Length - 1) / 2] + times[times.Length / 2]) / 2);
    }
}
