using System.Globalization;
using MediBridge.Core.X12;

namespace MediBridge.Core.PriorAuth;

public static class X12278Mapper
{
    public static PriorAuthMapping Map(string input)
    {
        if (input.Length > 65_536) return new(null, [new("PA_X12_LIMIT", "Input", "Input exceeds 64 KiB.")]);
        var interchanges = X12Tokenizer.Tokenize(input);
        List<PriorAuthError> errors = interchanges.SelectMany(t => t.Errors).Select(e => new PriorAuthError("PA_X12_SYNTAX", "Input", e.Reason,
            SegmentId: e.SegmentId, SegmentPosition: e.SegmentPosition, ElementPosition: e.ElementPosition)).ToList();
        if (interchanges.Count != 1) errors.Add(new("PA_X12_CARDINALITY", "Input", "Submit exactly one interchange, group, transaction and patient event per request."));
        if (errors.Count > 0) return new(null, errors);
        var tokens = interchanges[0]; var segments = tokens.Segments; var delimiter = tokens.Delimiters;
        int cursor = 0;
        X12Segment Take(string id)
        {
            if (cursor < segments.Count && segments[cursor].Id == id) return segments[cursor++];
            var position = cursor < segments.Count ? segments[cursor].Position : segments.Count + 1;
            errors.Add(new("PA_X12_STRUCTURE", "Input", $"Expected {id} in the documented order.", SegmentId: id, SegmentPosition: position, ElementPosition: 0));
            return new(id, position, []);
        }
        void Check(X12Segment segment, int element, bool valid, string reason)
        {
            if (!valid) errors.Add(new("PA_X12_STRUCTURE", "Input", reason, SegmentId: segment.Id, SegmentPosition: segment.Position, ElementPosition: element));
        }
        void Limit(X12Segment segment, int count) => Check(segment, count + 1, segment.Elements.Length <= count, "Unsupported extra element in this subset.");
        var isa = Take("ISA"); var gs = Take("GS"); var st = Take("ST"); var bht = Take("BHT");
        Check(isa, 12, isa.Element(12) == "00501", "Use ISA12=00501.");
        Check(isa, 15, isa.Element(15) == "T", "Use synthetic test interchange indicator ISA15=T.");
        Check(isa, 13, isa.Element(13).Length == 9 && isa.Element(13).All(char.IsAsciiDigit), "ISA13 must be nine ASCII digits.");
        Limit(isa, 16); Limit(gs, 8); Limit(st, 3); Limit(bht, 5);
        Check(gs, 1, gs.Element(1) == "HI", "Use GS01=HI.");
        Check(gs, 8, gs.Element(8) == "005010X217", "Use GS08=005010X217.");
        Check(st, 1, st.Element(1) == "278", "Use ST01=278.");
        Check(st, 2, st.Element(2).Length is >= 4 and <= 9 && st.Element(2).All(char.IsAsciiDigit), "ST02 must contain 4–9 ASCII digits.");
        Check(st, 3, st.Element(3) == "005010X217", "Use ST03=005010X217.");
        Check(bht, 1, bht.Element(1) == "0007", "Use BHT01=0007."); Check(bht, 2, bht.Element(2) == "13", "Use BHT02=13 (request).");
        var reviewHl = Take("HL"); Hierarchy(reviewHl, "1", "", "20", "1");
        var recipient = Take("NM1"); Name(recipient, "X3", "2", "PI");
        Check(recipient, 9, recipient.Element(9) == "SYN-SIMULATOR", "Use the fabricated recipient identifier SYN-SIMULATOR.");
        var providerHl = Take("HL"); Hierarchy(providerHl, "2", "1", "21", "1");
        var provider = Take("NM1"); Name(provider, "1P", "2", "XX");
        var memberHl = Take("HL"); Hierarchy(memberHl, "3", "2", "22", "1");
        var member = Take("NM1"); Name(member, "IL", "1", "MI");
        var eventHl = Take("HL"); Hierarchy(eventHl, "4", "3", "EV", "1");
        var um = Take("UM"); Limit(um, 6);
        Check(um, 1, um.Element(1) == "HS", "Use UM01=HS."); Check(um, 2, um.Element(2) == "I", "Use UM02=I (initial).");
        Check(um, 3, um.Element(3) == "30", "Use UM03=30 in this subset.");
        Check(um, 4, um.Element(4) == "" && um.Element(5) == "", "UM04 and UM05 are outside the subset; leave them empty.");
        List<string> codes = []; var servicePositions = new List<int>(); int serviceNumber = 5;
        while (cursor < segments.Count && segments[cursor].Id == "HL")
        {
            var hl = Take("HL"); Hierarchy(hl, (serviceNumber++).ToString(CultureInfo.InvariantCulture), "4", "SS", "0");
            var service = Take("SV1"); Limit(service, 1); servicePositions.Add(service.Position);
            var components = service.Element(1).Split(delimiter.Component);
            Check(service, 1, components.Length == 2 && components[0] == "HC", "Use SV101=HC:<fabricated SYN- service code> with the declared component delimiter.");
            codes.Add(components.Length == 2 ? components[1] : "");
            if (serviceNumber > 55) { errors.Add(new("PA_X12_LIMIT", "ServiceCodes", "At most 50 service loops are supported.")); break; }
        }
        var se = Take("SE"); var ge = Take("GE"); var iea = Take("IEA");
        Limit(se, 2); Limit(ge, 2); Limit(iea, 2);
        Check(se, 1, int.TryParse(se.Element(1), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count == se.Position - st.Position + 1, "SE01 must count ST through SE inclusively.");
        Check(se, 2, se.Element(2) == st.Element(2), "SE02 must match ST02.");
        Check(ge, 1, ge.Element(1) == "1", "Use one transaction per group (GE01=1)."); Check(ge, 2, gs.Element(6).Length > 0 && ge.Element(2) == gs.Element(6), "GE02 must match nonempty GS06.");
        Check(iea, 1, iea.Element(1) == "1", "Use one group per interchange (IEA01=1)."); Check(iea, 2, iea.Element(2) == isa.Element(13), "IEA02 must match ISA13.");
        if (cursor != segments.Count) errors.Add(new("PA_X12_STRUCTURE", "Input", "Unexpected trailing segments or multiple transactions.", SegmentId: segments[cursor].Id, SegmentPosition: segments[cursor].Position, ElementPosition: 0));
        if (errors.Count > 0) return new(null, errors);
        DateTimeOffset? submitted = DateTimeOffset.TryParseExact(bht.Element(4) + bht.Element(5), "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;
        var canonical = PriorAuthRules.Normalize(new(bht.Element(3), member.Element(9), provider.Element(9), codes, PriorAuthRules.LevelOfService(um.Element(6)), submitted));
        errors.AddRange(PriorAuthRules.Validate(canonical).Select(error => error.Field switch
        {
            "Id" => Locate(error, bht, 3), "MemberId" => Locate(error, member, 9), "RequestingProviderNpi" => Locate(error, provider, 9),
            "Priority" => Locate(error, um, 6), "SubmittedAt" => Locate(error, bht, 4),
            _ => error with { SegmentId = "SV1", SegmentPosition = servicePositions.FirstOrDefault(se.Position), ElementPosition = 1 }
        }));
        return new(canonical, errors);

        void Hierarchy(X12Segment s, string id, string parent, string type, string children)
        {
            Limit(s, 4);
            Check(s, 1, s.Element(1) == id, $"Expected hierarchy ID {id}."); Check(s, 2, s.Element(2) == parent, $"Expected parent hierarchy {parent}.");
            Check(s, 3, s.Element(3) == type, $"Expected hierarchy type {type}."); Check(s, 4, s.Element(4) == children, $"Expected child indicator {children}.");
        }
        void Name(X12Segment s, string entity, string kind, string qualifier)
        {
            Limit(s, 9); Check(s, 1, s.Element(1) == entity, $"Expected NM101={entity}."); Check(s, 2, s.Element(2) == kind, $"Expected NM102={kind}.");
            Check(s, 8, s.Element(8) == qualifier, $"Expected NM108={qualifier}.");
            Check(s, 3, Enumerable.Range(3, 5).All(i => s.Element(i) == ""), "Names and demographic fields are outside the synthetic subset; leave NM103–NM107 empty.");
        }
    }
    private static PriorAuthError Locate(PriorAuthError error, X12Segment s, int element) => error with { SegmentId = s.Id, SegmentPosition = s.Position, ElementPosition = element };
}
