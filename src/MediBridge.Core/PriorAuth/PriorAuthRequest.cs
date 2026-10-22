namespace MediBridge.Core.PriorAuth;

public enum PriorAuthPriority { Standard, Expedited }

public sealed record PriorAuthRequest(string Id, string MemberId, string RequestingProviderNpi,
    IReadOnlyList<string> ServiceCodes, PriorAuthPriority? Priority, DateTimeOffset? SubmittedAt);

public sealed record PriorAuthError(string RuleId, string Field, string Reason, string? Path = null,
    string? SegmentId = null, int? SegmentPosition = null, int? ElementPosition = null);

public sealed record PriorAuthMapping(PriorAuthRequest? Request, IReadOnlyList<PriorAuthError> Errors)
{
    public string Outcome => Request is not null && Errors.Count == 0 ? "Accepted" : "Rejected";
}

public static class PriorAuthRules
{
    public static PriorAuthRequest Normalize(PriorAuthRequest request) => request with
    {
        Id = request.Id.Trim(), MemberId = request.MemberId.Trim(), RequestingProviderNpi = request.RequestingProviderNpi.Trim(),
        ServiceCodes = request.ServiceCodes.Select(code => code.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        SubmittedAt = request.SubmittedAt?.ToUniversalTime()
    };

    public static IReadOnlyList<PriorAuthError> Validate(PriorAuthRequest request, Rules.RuleDefinition? definition = null) =>
        Rules.RuleEvaluator.Evaluate(definition ?? Rules.RuleCatalog.Baseline, Rules.RuleInput.From(request)).Failures
            .Select(f => new PriorAuthError(f.RuleId, f.Field, f.SuggestedFix)).ToArray();

    public static PriorAuthPriority? LevelOfService(string value) => value switch
    {
        "" or "E" => PriorAuthPriority.Standard, "U" => PriorAuthPriority.Expedited, _ => null
    };

    public static bool Equivalent(PriorAuthRequest? left, PriorAuthRequest? right) => left is null ? right is null : right is not null
        && left.Id == right.Id && left.MemberId == right.MemberId && left.RequestingProviderNpi == right.RequestingProviderNpi
        && left.ServiceCodes.SequenceEqual(right.ServiceCodes) && left.Priority == right.Priority && left.SubmittedAt == right.SubmittedAt;
}
