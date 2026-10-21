using System.Globalization;
using System.Text.Json;
using MediBridge.Core.PriorAuth;
using MediBridge.Infrastructure.PriorAuth;

namespace MediBridge.Parity;

public sealed record ParityCase(int Number, PriorAuthRequest Expected, string ExpectedOutcome, string[] ExpectedRules,
    string X12, string Fhir, string Variant);

public static class PairedRequests
{
    public static IReadOnlyList<ParityCase> Generate(int count = 200)
    {
        List<ParityCase> cases = [];
        for (var i = 0; i < count; i++)
        {
            var id = $"PA-PARITY-{i + 1:D4}"; var member = $"SYN-PA-{i + 1:D4}"; var npi = "0000000006";
            var codes = i % 2 == 0 ? new[] { "syn-service-b", "SYN-SERVICE-A", "syn-service-b" } : ["SYN-SERVICE-C"];
            var level = i % 2 == 0 ? "U" : i % 3 == 0 ? "E" : "";
            string[] rules = []; var variant = "valid";
            if (i % 5 == 4)
            {
                (variant, rules) = (i / 5 % 4) switch
                {
                    0 => ("invalid-member", new[] { "PA_MEMBER" }), 1 => ("invalid-npi", new[] { "PA_PROVIDER_NPI" }),
                    2 => ("invalid-services", new[] { "PA_SERVICES" }), _ => ("unsupported-priority", new[] { "PA_PRIORITY" })
                };
                if (variant == "invalid-member") member = "NOT-SYNTHETIC";
                if (variant == "invalid-npi") npi = "0000000000";
                if (variant == "invalid-services") codes = ["BAD-SERVICE"];
                if (variant == "unsupported-priority") level = "03";
            }
            var submitted = new DateTimeOffset(2026, 1, 20, 12, 0, 0, TimeSpan.Zero).AddSeconds(i);
            // Independent oracle built from source case data, not by calling either mapper or Core normalization.
            var expected = new PriorAuthRequest(id, member, npi, codes.Select(c => c.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray(),
                level == "U" ? PriorAuthPriority.Expedited : level == "03" ? null : PriorAuthPriority.Standard, submitted);
            var offset = i % 3 == 0 ? TimeSpan.FromMinutes(330) : i % 3 == 1 ? TimeSpan.FromHours(-4) : TimeSpan.Zero;
            cases.Add(new(i + 1, expected, rules.Length == 0 ? "Accepted" : "Rejected", rules,
                X12(id, member, npi, codes, level, submitted, i % 2 == 0), Fhir(id, member, npi, codes, level, submitted.ToOffset(offset), i % 2 == 0), variant));
        }
        return cases;
    }

    public static string X12(string id, string member, string npi, string[] codes, string level, DateTimeOffset submitted, bool alternate = false)
    {
        char e = alternate ? '|' : '*', c = alternate ? '>' : ':', r = alternate ? '!' : '^', t = alternate ? '?' : '~';
        string Segment(params string[] elements) => string.Join(e, elements);
        List<string> segments = [Segment("ISA", "00", new(' ', 10), "00", new(' ', 10), "ZZ", "SYN-SENDER".PadRight(15), "ZZ", "SYN-RECEIVER".PadRight(15), "260120", "1200", r.ToString(), "00501", "000000001", "0", "T", c.ToString()),
            Segment("GS", "HI", "SYN-SENDER", "SYN-RECEIVER", "20260120", "120000", "1", "X", "005010X217"),
            Segment("ST", "278", "0001", "005010X217"), Segment("BHT", "0007", "13", id, submitted.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), submitted.UtcDateTime.ToString("HHmmss", CultureInfo.InvariantCulture)),
            Segment("HL", "1", "", "20", "1"), Segment("NM1", "X3", "2", "", "", "", "", "", "PI", "SYN-SIMULATOR"),
            Segment("HL", "2", "1", "21", "1"), Segment("NM1", "1P", "2", "", "", "", "", "", "XX", npi),
            Segment("HL", "3", "2", "22", "1"), Segment("NM1", "IL", "1", "", "", "", "", "", "MI", member),
            Segment("HL", "4", "3", "EV", "1"), Segment("UM", "HS", "I", "30", "", "", level)];
        for (int index = 0; index < codes.Length; index++)
        {
            segments.Add(Segment("HL", (index + 5).ToString(CultureInfo.InvariantCulture), "4", "SS", "0"));
            segments.Add(Segment("SV1", "HC" + c + codes[index]));
        }
        segments.Add(Segment("SE", (segments.Count - 2 + 1).ToString(CultureInfo.InvariantCulture), "0001"));
        segments.Add(Segment("GE", "1", "1")); segments.Add(Segment("IEA", "1", "000000001"));
        return string.Join(t, segments) + t;
    }

    public static string Fhir(string id, string member, string npi, string[] codes, string level, DateTimeOffset submitted, bool contained = false)
    {
        object Identifier(string system, string value) => new { system, value };
        object Concept(string system, string code) => new { coding = new[] { new { system, code } } };
        var data = new Dictionary<string, object?>
        {
            ["resourceType"] = "Claim", ["id"] = id, ["identifier"] = new[] { Identifier(FhirPriorAuthMapper.RequestSystem, id) },
            ["status"] = "active", ["type"] = Concept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"), ["use"] = "preauthorization",
            ["patient"] = contained ? new { reference = "#member" } : (object)new { identifier = Identifier(FhirPriorAuthMapper.MemberSystem, member) },
            ["provider"] = contained ? new { reference = "#requestor" } : (object)new { identifier = Identifier(FhirPriorAuthMapper.NpiSystem, npi) },
            ["created"] = submitted.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["priority"] = Concept("http://terminology.hl7.org/CodeSystem/processpriority", "normal"),
            ["insurance"] = new[] { new { sequence = 1, focal = true, coverage = new { identifier = Identifier(FhirPriorAuthMapper.CoverageSystem, "SYN-COVERAGE") } } },
            ["item"] = codes.Select((code, index) => new { sequence = index + 1, productOrService = Concept(FhirPriorAuthMapper.ServiceSystem, code) }).ToArray()
        };
        if (level.Length > 0) data["extension"] = new[] { new { url = FhirPriorAuthMapper.LevelExtension, valueCodeableConcept = Concept(FhirPriorAuthMapper.LevelSystem, level) } };
        if (contained) data["contained"] = new object[]
        {
            new { resourceType = "Patient", id = "member", identifier = new[] { Identifier(FhirPriorAuthMapper.MemberSystem, member) } },
            new { resourceType = "Organization", id = "requestor", identifier = new[] { Identifier(FhirPriorAuthMapper.NpiSystem, npi) } }
        };
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }
}
