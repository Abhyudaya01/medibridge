using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediBridge.Core.PriorAuth;

namespace MediBridge.Core.Rules;

public sealed record RuleDefinition(string Version, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveUntil, IReadOnlyList<RuleClause> Rules, int SchemaVersion = 1);
public sealed record RuleClause(string Domain, string Id, string Field, string Operator, string Fix,
    int? MinLength = null, int? MaxLength = null, int? Length = null, int? MaxCount = null,
    decimal? Min = null, decimal? Max = null, int? Scale = null, string? Prefix = null,
    string? Alphabet = null, IReadOnlyList<string>? Values = null, string? When = null, IReadOnlyList<string>? PlanTypes = null);
public sealed record RuleResult(string Version, IReadOnlyList<ValidationFailure> Failures, string? Error = null)
{
    public string Outcome => Error is not null ? "Error" : Failures.Count == 0 ? "Accepted" : "Rejected";
}
// Captured independently of mutable claim processing. Collections are copied and read-only.
public sealed record RuleInput(string Domain, string Id, string Provider, string Member, IReadOnlyList<string> Codes,
    string PlanType, string RuleVersion, decimal Amount, DateOnly ServiceDate, DateTimeOffset? SubmittedAt, string? Priority)
{
    public static RuleInput From(Claim claim) => new("Claim", claim.Id ?? "", claim.ProviderNpi ?? "", claim.MemberId ?? "",
        Array.AsReadOnly((claim.ProcedureCodes ?? []).ToArray()), claim.PlanType ?? "Unspecified", claim.RuleVersion ?? "", claim.Amount, claim.ServiceDate, null, null);
    public static RuleInput From(PriorAuthRequest request, string plan = "Unspecified") => new("PriorAuth", request.Id, request.RequestingProviderNpi,
        request.MemberId, Array.AsReadOnly(request.ServiceCodes.ToArray()), plan, "", 0, default, request.SubmittedAt, request.Priority?.ToString());
    public string Text(string field) => field switch
    {
        "Id" => Id, "ProviderNpi" or "RequestingProviderNpi" => Provider, "MemberId" => Member,
        "RuleVersion" => RuleVersion, "PlanType" => PlanType, "Priority" => Priority ?? "", _ => ""
    };
}
public static class RuleCatalog
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static readonly RuleDefinition Baseline = Parse(new StreamReader(typeof(RuleCatalog).Assembly.GetManifestResourceStream("MediBridge.Rules.Baseline")!).ReadToEnd());
    public static readonly string[] Plans = ["Unspecified", "SYN-BASIC", "SYN-PLUS"];
    public static bool ValidPlan(string? plan) => plan is not null && Plans.Contains(plan, StringComparer.Ordinal);
    public static RuleDefinition Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 65536) throw new ArgumentException("Rule definition exceeds 64 KiB.");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        CheckDuplicates(doc.RootElement);
        var value = JsonSerializer.Deserialize<RuleDefinition>(json, Json) ?? throw new ArgumentException("Provide a definition object.");
        Validate(value); return value;
    }
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new ArgumentException("Duplicate JSON property."); CheckDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) CheckDuplicates(child);
    }
    public static void Validate(RuleDefinition definition)
    {
        void Require(bool valid, string message) { if (!valid) throw new ArgumentException(message); }
        Require(definition.SchemaVersion == 1, "Use schemaVersion 1.");
        Require(definition.Version is { Length: >= 1 and <= 32 } && definition.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'), "Use a version of 1–32 ASCII letters, digits, dots, hyphens or underscores.");
        Require(definition.EffectiveFrom != default && definition.EffectiveFrom.Offset == TimeSpan.Zero && (definition.EffectiveUntil is null || definition.EffectiveUntil.Value.Offset == TimeSpan.Zero && definition.EffectiveUntil > definition.EffectiveFrom), "Use a nonempty UTC effective window [effectiveFrom, effectiveUntil).");
        Require(definition.Rules is { Count: >= 1 and <= 128 }, "Provide 1–128 rules.");
        Require(definition.Rules!.Any(r => r is { Domain: "PriorAuth", Field: "Priority", Operator: "Allowed", When: null, PlanTypes: null, Values.Count: 2 }
            && r.Values.Contains("Standard", StringComparer.Ordinal) && r.Values.Contains("Expedited", StringComparer.Ordinal)), "Every version must retain an unconditional Standard/Expedited prior-auth priority constraint.");
        Require(definition.Rules.Any(r => r is { Domain: "PriorAuth", Field: "SubmittedAt", Operator: "DateRequired", When: null, PlanTypes: null }), "Every version must retain the required prior-auth submission timestamp constraint.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in definition.Rules!)
        {
            Require(r is not null, "Rules cannot contain null.");
            Require(r!.Domain is "Claim" or "PriorAuth", "Use Claim or PriorAuth domain.");
            Require(r.Id is { Length: >= 1 and <= 64 } && r.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && ids.Add(r.Domain + ":" + r.Id), "Rule IDs must be unique per domain and use ASCII letters, digits or underscores.");
            Require(r.Fix is { Length: >= 1 and <= 400 }, "Provide a fix of 1–400 characters.");
            var text = r.Domain == "Claim" ? new[] { "Id", "ProviderNpi", "MemberId", "RuleVersion", "PlanType" } : new[] { "Id", "RequestingProviderNpi", "MemberId", "Priority", "PlanType" };
            var isText = text.Contains(r.Field, StringComparer.Ordinal); var isCodes = r.Field == (r.Domain == "Claim" ? "ProcedureCodes" : "ServiceCodes");
            Require(r.Operator switch
            {
                "Required" or "MaxLength" or "Digits" or "Prefix" or "StringShape" or "Allowed" => isText,
                "NpiChecksum" => r.Field == (r.Domain == "Claim" ? "ProviderNpi" : "RequestingProviderNpi"),
                "CodesRequired" or "CodesShape" or "CodesForbidden" => isCodes,
                "Currency" => r.Domain == "Claim" && r.Field == "Amount",
                "DateRequired" => r.Field == (r.Domain == "Claim" ? "ServiceDate" : "SubmittedAt"), _ => false
            }, "Unsupported operator or field type.");
            Require(r.MinLength is null or >= 0 and <= 200 && r.MaxLength is null or >= 1 and <= 200 && r.Length is null or >= 1 and <= 200
                && r.MaxCount is null or >= 1 and <= 50 && r.Scale is null or >= 0 and <= 2, "Rule limits exceed supported bounds.");
            Require(r.MinLength is null || r.MaxLength is null || r.MinLength <= r.MaxLength, "Minimum length exceeds maximum.");
            Require(r.Alphabet is null or "Identifier" or "Printable", "Unknown alphabet.");
            Require(r.Prefix is null || r.Prefix.Length is >= 1 and <= 32, "Prefix must have 1–32 characters.");
            Require(r.Operator != "Prefix" || r.Prefix is not null, "Prefix operator requires prefix.");
            Require(r.Operator != "MaxLength" || r.MaxLength is not null, "MaxLength operator requires maxLength.");
            Require(r.Operator != "Digits" || r.Length is not null, "Digits operator requires length.");
            Require(r.Operator != "Currency" || r.Min is > 0 && r.Max >= r.Min && r.Max <= 9999999999999999.99m && r.Scale is not null, "Currency needs positive min, bounded max and scale.");
            Require(r.Values is null || r.Values.Count is >= 1 and <= 50 && r.Values.All(v => v is { Length: >= 1 and <= 64 }), "Values require 1–50 nonempty entries up to 64 characters.");
            Require(r.Operator is not ("Allowed" or "CodesForbidden") || r.Values is not null, "Operator requires values.");
            Require(r.PlanTypes is null || r.PlanTypes.Count is >= 1 and <= 3 && r.PlanTypes.All(ValidPlan), "Use supported synthetic plan types.");
            var parameters = r.Operator switch
            {
                "MaxLength" => new[] { "MaxLength" }, "Digits" => ["Length"], "Prefix" => ["Prefix"],
                "StringShape" => ["MinLength", "MaxLength", "Prefix", "Alphabet"],
                "CodesShape" => ["MinLength", "MaxLength", "Prefix", "Alphabet", "MaxCount"],
                "Currency" => ["Min", "Max", "Scale"], "Allowed" or "CodesForbidden" => ["Values"], _ => []
            };
            foreach (var property in new[] { nameof(r.MinLength), nameof(r.MaxLength), nameof(r.Length), nameof(r.MaxCount), nameof(r.Min), nameof(r.Max), nameof(r.Scale), nameof(r.Prefix), nameof(r.Alphabet), nameof(r.Values) })
                Require(typeof(RuleClause).GetProperty(property)!.GetValue(r) is null || parameters.Contains(property), "Parameter is incompatible with operator.");
            Require(r.When is null || r.When == "Present" && isText || r.When == "Digits10" && isText || r.When == "CodesPresent" && isCodes, "Unknown or incompatible condition.");
        }
    }
}
public static class RuleEvaluator
{
    public static RuleResult Evaluate(RuleDefinition definition, RuleInput input)
    {
        List<ValidationFailure> failures = [];
        foreach (var rule in definition.Rules.Where(r => r.Domain == input.Domain))
        {
            if (rule.PlanTypes is not null && !rule.PlanTypes.Contains(input.PlanType, StringComparer.Ordinal)) continue;
            var text = input.Text(rule.Field);
            if (rule.When == "Present" && string.IsNullOrWhiteSpace(text) || rule.When == "Digits10" && !Digits(text, 10)
                || rule.When == "CodesPresent" && (input.Codes.Count == 0 || input.Codes.Any(string.IsNullOrWhiteSpace))) continue;
            var valid = rule.Operator switch
            {
                "Required" => !string.IsNullOrWhiteSpace(text),
                "MaxLength" => text.Length <= rule.MaxLength,
                "Digits" => Digits(text, rule.Length!.Value),
                "NpiChecksum" => ClaimRules.HasValidNpiChecksum(text),
                "Prefix" => text.StartsWith(rule.Prefix!, StringComparison.Ordinal),
                "StringShape" => Shape(text, rule),
                "Allowed" => rule.Values!.Contains(text, StringComparer.Ordinal),
                "CodesRequired" => input.Codes.Count > 0 && input.Codes.All(c => !string.IsNullOrWhiteSpace(c)),
                "CodesShape" => input.Codes.Count > 0 && (rule.MaxCount is null || input.Codes.Count <= rule.MaxCount) && input.Codes.All(c => Shape(c ?? "", rule)),
                "CodesForbidden" => !input.Codes.Any(c => rule.Values!.Contains((c ?? "").Trim(), StringComparer.OrdinalIgnoreCase)),
                "Currency" => input.Amount >= rule.Min && input.Amount <= rule.Max && decimal.Round(input.Amount, rule.Scale!.Value) == input.Amount,
                "DateRequired" => input.Domain == "Claim" ? input.ServiceDate != default : input.SubmittedAt is not null && input.SubmittedAt != DateTimeOffset.MinValue,
                _ => throw new InvalidOperationException("Unknown rule operator.")
            };
            if (!valid) failures.Add(new(rule.Id, rule.Field, rule.Fix));
        }
        return new(definition.Version, failures.AsReadOnly());
    }
    private static bool Digits(string text, int length) => text.Length == length && text.All(char.IsAsciiDigit);
    private static bool Shape(string text, RuleClause rule) => (rule.MinLength is null || text.Length >= rule.MinLength)
        && (rule.MaxLength is null || text.Length <= rule.MaxLength) && (rule.Prefix is null || text.StartsWith(rule.Prefix, StringComparison.Ordinal))
        && (rule.Alphabet != "Printable" || text.All(c => c is >= '!' and <= '~'))
        && (rule.Alphabet != "Identifier" || text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
}
