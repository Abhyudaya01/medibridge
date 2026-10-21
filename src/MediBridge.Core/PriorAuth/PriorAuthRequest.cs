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

    public static IReadOnlyList<PriorAuthError> Validate(PriorAuthRequest request)
    {
        List<PriorAuthError> errors = [];
        if (request.Id.Length is < 1 or > 64 || request.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            errors.Add(new("PA_ID", "Id", "Provide a request identifier of 1–64 ASCII letters, digits, dots, hyphens or underscores."));
        if (request.MemberId.Length is < 5 or > 64 || !request.MemberId.StartsWith("SYN-", StringComparison.Ordinal) || request.MemberId.Any(c => c < '!' || c > '~'))
            errors.Add(new("PA_MEMBER", "MemberId", "Provide a fabricated member identifier beginning with SYN-, up to 64 printable ASCII characters without spaces."));
        if (!ClaimRules.HasValidNpiChecksum(request.RequestingProviderNpi))
            errors.Add(new("PA_PROVIDER_NPI", "RequestingProviderNpi", "Provide a fabricated 10-digit NPI with the 80840-prefix Luhn checksum; demo: 0000000006."));
        if (request.ServiceCodes.Count is < 1 or > 50 || request.ServiceCodes.Any(code => code.Length is < 5 or > 32 || !code.StartsWith("SYN-", StringComparison.Ordinal)
            || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
            errors.Add(new("PA_SERVICES", "ServiceCodes", "Provide 1–50 distinct fabricated SYN- service codes, each up to 32 ASCII letters, digits, dots, hyphens or underscores."));
        if (request.Priority is null || !Enum.IsDefined(request.Priority.Value))
            errors.Add(new("PA_PRIORITY", "Priority", "Use routine/elective (standard) or urgent (expedited) in the documented subset."));
        if (request.SubmittedAt is null || request.SubmittedAt == DateTimeOffset.MinValue)
            errors.Add(new("PA_SUBMITTED_AT", "SubmittedAt", "Provide a complete submitted timestamp with seconds and an explicit UTC offset; X12 BHT timestamps are interpreted as UTC."));
        return errors;
    }

    public static PriorAuthPriority? LevelOfService(string value) => value switch
    {
        "" or "E" => PriorAuthPriority.Standard, "U" => PriorAuthPriority.Expedited, _ => null
    };

    public static bool Equivalent(PriorAuthRequest? left, PriorAuthRequest? right) => left is null ? right is null : right is not null
        && left.Id == right.Id && left.MemberId == right.MemberId && left.RequestingProviderNpi == right.RequestingProviderNpi
        && left.ServiceCodes.SequenceEqual(right.ServiceCodes) && left.Priority == right.Priority && left.SubmittedAt == right.SubmittedAt;
}
