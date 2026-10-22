using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Utility;
using MediBridge.Core.PriorAuth;
using FhirClaim = Hl7.Fhir.Model.Claim;

namespace MediBridge.Infrastructure.PriorAuth;

public static class FhirPriorAuthMapper
{
    public const string RequestSystem = "urn:medibridge:prior-auth";
    public const string MemberSystem = "urn:medibridge:synthetic-member";
    public const string CoverageSystem = "urn:medibridge:synthetic-coverage";
    public const string ServiceSystem = "urn:medibridge:synthetic-service";
    public const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    public const string LevelExtension = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-levelOfServiceCode";
    public const string LevelSystem = "https://codesystem.x12.org/005010/1338";

    public static PriorAuthMapping Map(string input, MediBridge.Core.Rules.RuleDefinition? definition = null, string planType = "Unspecified")
    {
        List<PriorAuthError> errors = [];
        if (System.Text.Encoding.UTF8.GetByteCount(input) > 65_536) return Fail("PA_FHIR_LIMIT", "Input", "Input exceeds 64 KiB.", "$");
        try
        {
            using var json = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return Fail("PA_FHIR_JSON", "Input", "Submit a JSON object containing a Claim resource.", "$");
            CheckJson(json.RootElement, "$");
            if (json.RootElement.ValueKind == JsonValueKind.Object)
                Allowed(json.RootElement, "$", ["resourceType", "id", "meta", "identifier", "status", "type", "use", "patient", "created", "provider", "priority", "insurance", "item", "extension", "contained"]);
            if (errors.Count > 0) return new(null, errors);
            var parser = new BaseFhirJsonDeserializer(ModelInfo.ModelInspector);
            Resource? resource;
            IEnumerable<CodedException> issues;
            bool parsedResource;
            try { parsedResource = parser.TryDeserializeResource(input, out resource, out issues); }
            catch (InvalidOperationException) { return Fail("PA_FHIR_PARSE", "Input", "A FHIR element has an unsupported JSON shape.", "Claim"); }
            if (!parsedResource)
                return new(null, issues.Select(issue => new PriorAuthError("PA_FHIR_PARSE", "Input", "Invalid FHIR R4 element (" + issue.ErrorCode + ").", issue is ExtendedCodedException extended ? extended.InstancePath : "Claim")).Take(30).ToArray());
            if (resource is not FhirClaim claim)
                return Fail("PA_FHIR_RESOURCE", "Input", "Submit a single FHIR R4 Claim resource.", "resourceType");
            Require(claim.Status == FinancialResourceStatusCodes.Active, "status", "Use status=active.");
            Require(claim.Use == ClaimUseCode.Preauthorization, "use", "Use use=preauthorization.");
            Require(SingleCoding(claim.Type, "http://terminology.hl7.org/CodeSystem/claim-type", "professional"), "type", "Use the professional claim type.");
            Require(SingleCoding(claim.Priority, "http://terminology.hl7.org/CodeSystem/processpriority", "normal"), "priority", "Use normal processing priority; requested urgency is supplied by the PAS level-of-service extension.");
            Require(claim.ModifierExtension.Count == 0, "modifierExtension", "Modifier extensions are outside this subset.");
            Require(claim.Extension.All(e => e.Url == LevelExtension) && claim.Extension.Count <= 1, "extension", "Only one PAS level-of-service extension is supported.");
            Require(claim.Contained.All(r => r is Patient or Organization or Practitioner), "contained", "Only contained Patient, Organization and Practitioner identifiers are supported.");
            Require(claim.Contained.All(r => !string.IsNullOrEmpty(r.Id)) && claim.Contained.Select(r => r.Id).Distinct().Count() == claim.Contained.Count,
                "contained", "Contained resources need distinct IDs.");
            Require(claim.Contained.OfType<DomainResource>().All(r => r.ModifierExtension.Count == 0), "contained.modifierExtension", "Contained modifier extensions are outside this subset.");
            Require(claim.Insurance.Count == 1 && claim.Insurance[0].Sequence == 1 && claim.Insurance[0].Focal == true
                && claim.Insurance[0].Coverage?.Reference is null && claim.Insurance[0].Coverage?.Identifier?.System == CoverageSystem
                && claim.Insurance[0].Coverage?.Identifier?.Value?.StartsWith("SYN-", StringComparison.Ordinal) == true,
                "insurance", "Provide one focal insurance entry (sequence=1) with a logical synthetic coverage identifier; no external reference is resolved.");
            Require(claim.Item.Count is >= 1 and <= 50 && claim.Item.All(item => item.Sequence > 0)
                && claim.Item.Select(item => item.Sequence).Distinct().Count() == claim.Item.Count, "item", "Provide 1–50 items with distinct positive sequences.");
            var id = Identifier(claim.Identifier, RequestSystem, "identifier");
            var member = Resolve(claim.Patient, MemberSystem, "patient", true);
            var provider = Resolve(claim.Provider, NpiSystem, "provider", false);
            var codes = new List<string>();
            for (int i = 0; i < claim.Item.Count; i++)
            {
                var coding = claim.Item[i].ProductOrService?.Coding;
                Require(coding?.Count == 1 && coding[0].System == ServiceSystem, $"item[{i}].productOrService", "Use one coding from the synthetic service system per item.");
                codes.Add(coding?.Count == 1 ? coding[0].Code ?? "" : "");
            }
            var level = "";
            if (claim.Extension.Count == 1 && claim.Extension[0].Url == LevelExtension)
            {
                var concept = claim.Extension[0].Value as CodeableConcept;
                Require(concept?.Coding.Count == 1 && concept.Coding[0].System == LevelSystem && claim.Extension[0].Extension.Count == 0,
                    "extension[0].valueCodeableConcept", "Use one X12 level-of-service coding.");
                level = concept?.Coding.Count == 1 ? concept.Coding[0].Code ?? "?" : "?";
            }
            DateTimeOffset? submitted = null;
            if (claim.Created is not null && Regex.IsMatch(claim.Created, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)
                && DateTimeOffset.TryParse(claim.Created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) submitted = parsed.ToUniversalTime();
            if (errors.Count > 0) return new(null, errors);
            var canonical = PriorAuthRules.Normalize(new(id, member, provider, codes, PriorAuthRules.LevelOfService(level), submitted));
            errors.AddRange(MediBridge.Core.Rules.RuleEvaluator.Evaluate(definition ?? MediBridge.Core.Rules.RuleCatalog.Baseline, MediBridge.Core.Rules.RuleInput.From(canonical, planType)).Failures.Select(f => new PriorAuthError(f.RuleId, f.Field, f.SuggestedFix)).Select(e => e with { Path = e.Field switch
            {
                "Id" => "Claim.identifier", "MemberId" => "Claim.patient", "RequestingProviderNpi" => "Claim.provider", "ServiceCodes" => "Claim.item.productOrService",
                "Priority" => "Claim.extension[levelOfServiceCode]", _ => "Claim.created"
            } }));
            return new(canonical, errors);

            string Identifier(IEnumerable<Identifier> identifiers, string system, string path)
            {
                var list = identifiers.ToList();
                Require(list.Count == 1 && list[0].System == system, path, $"Use exactly one identifier with system {system}.");
                return list.Count == 1 ? list[0].Value ?? "" : "";
            }
            string Resolve(ResourceReference? reference, string system, string path, bool patient)
            {
                if (reference is null) { Require(false, path, "A logical identifier or contained reference is required."); return ""; }
                string? contained = null;
                if (reference.Reference is not null)
                {
                    var found = reference.Reference.StartsWith('#') ? claim.Contained.Where(r => "#" + r.Id == reference.Reference).ToArray() : [];
                    if (found.Length != 1 || (patient ? found[0] is not Patient : found[0] is not (Organization or Practitioner)))
                    { Require(false, path + ".reference", "Use a matching contained resource; external references are not fetched."); return ""; }
                    var ids = found[0] switch { Patient p => p.Identifier, Organization o => o.Identifier, Practitioner p => p.Identifier, _ => [] };
                    contained = Identifier(ids, system, path + ".reference.identifier");
                }
                if (reference.Type is not null) Require(patient ? reference.Type == "Patient" : reference.Type is "Organization" or "Practitioner", path + ".type", "Reference type does not match the requested identity.");
                if (reference.Identifier is null)
                {
                    Require(contained is not null, path, "Supply an identifier or a contained reference."); return contained ?? "";
                }
                var logical = Identifier([reference.Identifier], system, path + ".identifier");
                Require(contained is null || logical == contained, path, "Logical and contained identifiers disagree."); return logical;
            }
        }
        catch (JsonException e) { return Fail("PA_FHIR_JSON", "Input", "Malformed JSON, invalid Unicode or excessive nesting.", e.Path ?? "$"); }
        catch (FormatException) { return Fail("PA_FHIR_PARSE", "Input", "Invalid FHIR R4 JSON element.", "Claim"); }

        void Require(bool condition, string path, string reason) { if (!condition) errors.Add(new("PA_FHIR_SUBSET", "Input", reason, "Claim." + path)); }
        void Allowed(JsonElement value, string path, string[] allowed)
        {
            foreach (var property in value.EnumerateObject())
                if (!allowed.Contains(property.Name)) errors.Add(new("PA_FHIR_SUBSET", "Input", "This element is outside the documented intake subset.", path + "." + property.Name));
        }
        void CheckJson(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                if (Regex.IsMatch(path, @"^\$\.item\[\d+\]$")) Allowed(element, path, ["sequence", "productOrService"]);
                if (Regex.IsMatch(path, @"^\$\.insurance\[\d+\]$")) Allowed(element, path, ["sequence", "focal", "coverage"]);
                if (path is "$.patient" or "$.provider") Allowed(element, path, ["reference", "type", "identifier"]);
                if (path.EndsWith(".coverage", StringComparison.Ordinal)) Allowed(element, path, ["identifier"]);
                if (path.EndsWith(".productOrService", StringComparison.Ordinal) || path is "$.type" or "$.priority") Allowed(element, path, ["coding"]);
                if (Regex.IsMatch(path, @"\.coding\[\d+\]$")) Allowed(element, path, ["system", "code"]);
                if (Regex.IsMatch(path, @"\.identifier(\[\d+\])?$")) Allowed(element, path, ["system", "value"]);
                if (path.Contains(".contained[", StringComparison.Ordinal) && element.TryGetProperty("resourceType", out _))
                    Allowed(element, path, ["resourceType", "id", "identifier"]);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) errors.Add(new("PA_FHIR_JSON", "Input", "Duplicate JSON property is ambiguous.", path + "." + property.Name));
                    if (property.Name == "modifierExtension") errors.Add(new("PA_FHIR_SUBSET", "Input", "Modifier extensions are not supported.", path + ".modifierExtension"));
                    CheckJson(property.Value, path + "." + property.Name);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            { int index = 0; foreach (var child in element.EnumerateArray()) CheckJson(child, path + $"[{index++}]"); }
        }
    }
    private static bool SingleCoding(CodeableConcept? value, string system, string code) => value?.Coding.Count == 1 && value.Coding[0].System == system && value.Coding[0].Code == code;
    private static PriorAuthMapping Fail(string rule, string field, string reason, string path) => new(null, [new(rule, field, reason, path)]);
}
