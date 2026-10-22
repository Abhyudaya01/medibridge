using System.Globalization;

namespace MediBridge.Core.X12;

public static class X12Parser
{
    public static IReadOnlyList<X12Interchange> Parse(string input, bool validateDomain = true) => X12Tokenizer.Tokenize(input).Select(tokens => new Reader(tokens, validateDomain).Read()).ToArray();

    private sealed class Reader(X12Tokens tokens, bool validateDomain)
    {
        private readonly List<X12Error> _errors = [.. tokens.Errors];
        private readonly List<X12ParsedClaim> _claims = [];
        private readonly List<X12Transaction> _transactions = [];
        private readonly HashSet<string> _groupIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _transactionIds = new(StringComparer.Ordinal);
        private X12Segment? _group, _transaction, _provider, _subscriber;
        private Draft? _claim;
        private bool _bht;
        private int _groupCount, _transactionCount, _claimCount;
        private string? _control;

        public X12Interchange Read()
        {
            if (tokens.Segments.Count == 0) return Result();
            var isa = tokens.Segments[0];
            _control = isa.Element(13);
            Count(isa, 16, 16);
            Expect(isa, 1, "00"); Expect(isa, 3, "00");
            Expect(isa, 5, "ZZ"); Expect(isa, 7, "ZZ");
            Required(isa, 6); Required(isa, 8);
            Expect(isa, 12, "00501"); Expect(isa, 15, "T");
            if (_control.Length != 9 || !_control.All(char.IsAsciiDigit)) Error(isa, 13, "Interchange control number must contain exactly nine digits.");
            if (!DateTime.TryParseExact(isa.Element(9), "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) Error(isa, 9, "Use a valid YYMMDD interchange date.");
            Time(isa, 10);
            if (isa.Element(14) is not ("0" or "1")) Error(isa, 14, "Acknowledgment requested indicator must be 0 or 1.");
            foreach (var segment in tokens.Segments.Skip(1))
            {
                for (var element = 1; element <= segment.Elements.Length; element++)
                {
                        if (segment.Element(element).Contains(tokens.Delimiters.Repetition))
                            Error(segment, element, "Repeated elements are not supported by this subset.");
                    var composite = segment.Id == "HI" || (segment.Id == "CLM" && element == 5) || (segment.Id == "SV1" && element == 1);
                    if (!composite && segment.Element(element).Contains(tokens.Delimiters.Component))
                        Error(segment, element, "A component delimiter is not supported in this scalar element.");
                }
                switch (segment.Id)
                {
                    case "GS":
                        if (_group is not null || _transaction is not null) Error(segment, 0, "Close the preceding transaction and functional group before GS.");
                        _group = segment; _groupCount++; _transactionCount = 0;
                        Count(segment, 8, 8); Expect(segment, 1, "HC"); Required(segment, 2); Required(segment, 3);
                        Date(segment, 4); Time(segment, 5); Required(segment, 6); Expect(segment, 7, "X"); Expect(segment, 8, "005010X222A1");
                        if (segment.Element(6).Length is < 1 or > 9 || !segment.Element(6).All(char.IsAsciiDigit)) Error(segment, 6, "Group control number must contain 1–9 digits.");
                        if (!_groupIds.Add(segment.Element(6))) Error(segment, 6, "Functional group control numbers must be unique within the interchange.");
                        break;
                    case "ST":
                        if (_group is null || _transaction is not null) Error(segment, 0, "ST must occur inside an open GS and outside another transaction.");
                        _transaction = segment; _transactionCount++; _claimCount = 0; _bht = false; _provider = null; _subscriber = null; _claim = null;
                        Count(segment, 3, 3); Expect(segment, 1, "837"); Expect(segment, 3, "005010X222A1");
                        if (segment.Element(2).Length is < 4 or > 9 || !segment.Element(2).All(char.IsAsciiDigit)) Error(segment, 2, "Transaction control number must contain 4–9 digits.");
                        if (!_transactionIds.Add($"{_group?.Element(6)}:{segment.Element(2)}")) Error(segment, 2, "Transaction control numbers must be unique within a functional group.");
                        _transactions.Add(new(_group?.Element(6) ?? "", segment.Element(2), segment.Position));
                        break;
                    case "BHT":
                        InTransaction(segment);
                        if (_bht || _provider is not null || _claimCount > 0) Error(segment, 0, "BHT must occur once, immediately after ST.");
                        Count(segment, 6, 6); Expect(segment, 1, "0019"); Expect(segment, 2, "00"); Required(segment, 3);
                        Date(segment, 4); Time(segment, 5); Expect(segment, 6, "CH"); _bht = true;
                        break;
                    case "NM1": ReadName(segment); break;
                    case "CLM": BeginClaim(segment); break;
                    case "HI": ReadDiagnosis(segment); break;
                    case "SV1": ReadService(segment); break;
                    case "DTP": ReadDate(segment); break;
                    case "SE":
                        InTransaction(segment); FinishClaim(); Count(segment, 2, 2);
                        if (!_bht) Error(segment, 0, "Transaction requires BHT before claim data.");
                        if (_claimCount == 0) Error(segment, 0, "Transaction requires at least one CLM.");
                        MatchCount(segment, 1, _transaction is null ? 0 : segment.Position - _transaction.Position + 1);
                        if (segment.Element(2) != _transaction?.Element(2)) Error(segment, 2, "SE02 must match ST02.");
                        _transaction = null; _provider = null; _subscriber = null;
                        break;
                    case "GE":
                        if (_group is null || _transaction is not null) Error(segment, 0, "GE requires an open GS and all ST transactions closed by SE.");
                        if (_transactionCount == 0) Error(segment, 1, "Functional group requires at least one ST transaction.");
                        Count(segment, 2, 2); MatchCount(segment, 1, _transactionCount);
                        if (segment.Element(2) != _group?.Element(6)) Error(segment, 2, "GE02 must match GS06.");
                        _group = null;
                        break;
                    case "IEA":
                        if (_group is not null || _transaction is not null) Error(segment, 0, "Close SE and GE before IEA.");
                        if (_groupCount == 0) Error(segment, 1, "Interchange requires at least one GS functional group.");
                        Count(segment, 2, 2); MatchCount(segment, 1, _groupCount);
                        if (segment.Element(2) != _control) Error(segment, 2, "IEA02 must match ISA13.");
                        break;
                    default: Error(segment, 0, "Segment is unsupported by the documented MediBridge 837P subset."); break;
                }
            }
            if (_transaction is not null) Error(_transaction, 0, "Transaction has no matching SE trailer.");
            if (_group is not null) Error(_group, 0, "Functional group has no matching GE trailer.");
            return Result();
        }

        private void ReadName(X12Segment segment)
        {
            InTransaction(segment);
            if (!_bht) Error(segment, 0, "BHT must precede NM1.");
            FinishClaim();
            Count(segment, 9, 9);
            if (segment.Element(1) == "85")
            {
                Expect(segment, 2, "2"); Required(segment, 3); Expect(segment, 8, "XX"); if (validateDomain) Required(segment, 9);
                if (validateDomain && !ClaimRules.HasValidNpiChecksum(segment.Element(9))) Error(segment, 9, "Billing provider requires a 10-digit synthetic NPI with a valid 80840-prefixed Luhn checksum.");
                _provider = segment; _subscriber = null;
            }
            else if (segment.Element(1) == "IL")
            {
                if (_provider is null) Error(segment, 1, "Billing provider NM1*85 must precede subscriber NM1*IL.");
                Expect(segment, 2, "1"); Required(segment, 3); Expect(segment, 8, "MI"); if (validateDomain) Required(segment, 9);
                if (validateDomain && (!segment.Element(9).StartsWith("SYN-", StringComparison.Ordinal) || segment.Element(9).Length > 64))
                    Error(segment, 9, "Use a fabricated member ID beginning SYN-, at most 64 characters.");
                _subscriber = segment;
            }
            else Error(segment, 1, "Only billing-provider 85 and subscriber IL NM1 qualifiers are supported.");
        }

        private void BeginClaim(X12Segment segment)
        {
            InTransaction(segment); FinishClaim();
            if (!_bht || _provider is null || _subscriber is null) Error(segment, 0, "CLM requires BHT, billing-provider NM1*85, and subscriber NM1*IL context.");
            Count(segment, 9, 9); Required(segment, 1);
            if (segment.Element(3).Length > 0) Error(segment, 3, "CLM03 must be empty in this subset.");
            if (segment.Element(4).Length > 0) Error(segment, 4, "CLM04 must be empty in this subset.");
            if (segment.Element(1).Length > 64 || segment.Element(1).Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                Error(segment, 1, "Use a claim ID of at most 64 ASCII letters, digits, hyphens, or underscores.");
            var amount = Money(segment, 2);
            var place = Composite(segment, 5);
            if (place.Length != 3 || place[0] != "11" || place[1] != "B" || place[2] != "1") Error(segment, 5, "Use place-of-service composite 11:B:1 with the declared component delimiter.");
            Expect(segment, 6, "Y"); Expect(segment, 7, "A"); Expect(segment, 8, "Y"); Expect(segment, 9, "Y");
            _claimCount++;
            _claim = new(segment, new Claim
            {
                Id = segment.Element(1), ProviderNpi = _provider?.Element(9) ?? "", MemberId = _subscriber?.Element(9) ?? "",
                Amount = amount
            }, _provider, _subscriber, _group?.Element(6) ?? "", _transaction?.Element(2) ?? "");
        }

        private void ReadDiagnosis(X12Segment segment)
        {
            InTransaction(segment);
            if (_claim is null) { Error(segment, 0, "HI requires an open CLM."); return; }
            if (_claim.Services.Count > 0 || _claim.Diagnoses.Count > 0) Error(segment, 0, "HI occurs once per claim, before SV1 service lines.");
            Count(segment, 1, 12);
            for (var element = 1; element <= segment.Elements.Length; element++)
            {
                var diagnosis = Composite(segment, element);
                if (diagnosis.Length != 2 || diagnosis[0] != (element == 1 ? "ABK" : "ABF") || string.IsNullOrWhiteSpace(diagnosis.ElementAtOrDefault(1)))
                    Error(segment, element, "Use ABK:code for the first diagnosis and ABF:code for subsequent diagnoses, with the declared component delimiter.");
                else if (diagnosis[1].Length > 8 || !diagnosis[1].All(char.IsAsciiLetterOrDigit))
                    Error(segment, element, "Synthetic diagnosis codes must contain 1–8 ASCII letters or digits.", 2);
                else _claim.Diagnoses.Add(diagnosis[1]);
            }
        }

        private void ReadService(X12Segment segment)
        {
            InTransaction(segment);
            if (_claim is null) { Error(segment, 0, "SV1 requires an open CLM."); return; }
            Count(segment, 4, 4);
            var procedure = Composite(segment, 1);
            if (procedure.Length != 2 || procedure[0] != "HC" || string.IsNullOrWhiteSpace(procedure.ElementAtOrDefault(1)))
                Error(segment, 1, "Use HC:procedure with the declared component delimiter; modifiers are outside this subset.");
            else if (procedure[1].Length > 32) Error(segment, 1, "Procedure code cannot exceed 32 characters.", 2);
            else _claim.Claim.ProcedureCodes.Add(procedure[1]);
            var charge = Money(segment, 2);
            Expect(segment, 3, "UN");
            if (!decimal.TryParse(segment.Element(4), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var units) || units <= 0)
                Error(segment, 4, "Provide a positive invariant-decimal unit count.");
            _claim.Services.Add((segment, charge));
        }

        private void ReadDate(X12Segment segment)
        {
            InTransaction(segment);
            if (_claim is null) { Error(segment, 0, "DTP requires an open CLM."); return; }
            Count(segment, 3, 3); Expect(segment, 1, "472"); Expect(segment, 2, "D8");
            var date = Date(segment, 3);
            var scope = _claim.Services.Count; // zero = claim date, otherwise the preceding service line.
            if (!_claim.DatedScopes.Add(scope)) Error(segment, 3, "Only one DTP is supported per claim or service-line scope.");
            if (_claim.Date is not null && _claim.Date != date) Error(segment, 3, "All service dates must agree; Core Claim supports one ServiceDate.");
            _claim.Date = date; _claim.DateSource = segment;
        }

        private void FinishClaim()
        {
            if (_claim is null) return;
            var draft = _claim;
            if (draft.Services.Count == 0) Error(draft.Source, 0, "Claim requires at least one SV1 service line.");
            if (draft.Services.Count > 50) Error(draft.Source, 0, "Claim cannot contain more than 50 service lines.");
            if (draft.Diagnoses.Count == 0) Error(draft.Source, 0, "Claim requires HI diagnosis information before service lines.");
            if (draft.Date is null) Error(draft.Source, 0, "Claim requires DTP*472*D8 service date information.");
            if (!draft.DatedScopes.Contains(0) && Enumerable.Range(1, draft.Services.Count).Any(scope => !draft.DatedScopes.Contains(scope)))
                Error(draft.Source, 0, "Provide a claim-level service date or a DTP for every service line.");
            // Bound charges before summation to prevent decimal overflow even on malformed input.
            var total = draft.Services.Sum(service => service.Charge);
            if (total != draft.Claim.Amount) Error(draft.Source, 2, "CLM02 must equal the sum of SV102 line charges; SV102 is already the extended line charge.");
            draft.Claim.ServiceDate = draft.Date ?? default;
            foreach (var failure in validateDomain ? ClaimRules.Validate(draft.Claim) : [])
            {
                var (source, element) = failure.Field switch
                {
                    "ProviderNpi" => (draft.Provider ?? draft.Source, draft.Provider is null ? 0 : 9),
                    "MemberId" => (draft.Subscriber ?? draft.Source, draft.Subscriber is null ? 0 : 9),
                    "Amount" => (draft.Source, 2), "Id" => (draft.Source, 1),
                    "ServiceDate" => (draft.DateSource ?? draft.Source, draft.DateSource is null ? 0 : 3),
                    _ => (draft.Source, 0)
                };
                Error(source, element, $"{failure.RuleId}: {failure.SuggestedFix}");
            }
            _claims.Add(new(draft.Claim, draft.Diagnoses, draft.Source.Position, draft.GroupControl, draft.TransactionControl,
                new Dictionary<string, X12FieldLocation>
                {
                    ["Id"] = Location(draft.Source, 1), ["Amount"] = Location(draft.Source, 2),
                    ["ProviderNpi"] = Location(draft.Provider ?? draft.Source, draft.Provider is null ? 0 : 9),
                    ["MemberId"] = Location(draft.Subscriber ?? draft.Source, draft.Subscriber is null ? 0 : 9),
                    ["ServiceDate"] = Location(draft.DateSource ?? draft.Source, draft.DateSource is null ? 0 : 3),
                    ["ProcedureCodes"] = Location(draft.Source, 0)
                }));
            static X12FieldLocation Location(X12Segment segment, int element) => new(segment.Id, segment.Position, element);
            _claim = null;
        }

        private X12Interchange Result() => new(_control, tokens.Delimiters, _transactions, _claims, _errors);
        private void InTransaction(X12Segment segment)
        {
            if (_transaction is null) Error(segment, 0, "Segment must occur inside ST/SE.");
        }
        private string[] Composite(X12Segment segment, int element) => segment.Element(element).Split(tokens.Delimiters.Component, StringSplitOptions.None);
        private void Required(X12Segment segment, int element)
        {
            if (string.IsNullOrWhiteSpace(segment.Element(element))) Error(segment, element, "Required element is missing or empty.");
        }
        private void Expect(X12Segment segment, int element, string expected)
        {
            if (segment.Element(element) != expected) Error(segment, element, $"Expected {expected} in this subset.");
        }
        private void Count(X12Segment segment, int minimum, int maximum)
        {
            if (segment.Elements.Length < minimum) Error(segment, segment.Elements.Length + 1, $"Segment requires at least {minimum} elements, including empty placeholders.");
            if (segment.Elements.Length > maximum) Error(segment, maximum + 1, $"Segment supports at most {maximum} elements in this subset.");
        }
        private void MatchCount(X12Segment segment, int element, int count)
        {
            if (!int.TryParse(segment.Element(element), NumberStyles.None, CultureInfo.InvariantCulture, out var received) || received != count)
                Error(segment, element, $"Trailer count must equal {count}.");
        }
        private DateOnly Date(X12Segment segment, int element)
        {
            if (DateOnly.TryParseExact(segment.Element(element), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
            Error(segment, element, "Use a valid YYYYMMDD date."); return default;
        }
        private void Time(X12Segment segment, int element)
        {
            if (!TimeOnly.TryParseExact(segment.Element(element), "HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) Error(segment, element, "Use a valid HHMM time.");
        }
        private decimal Money(X12Segment segment, int element)
        {
            if (decimal.TryParse(segment.Element(element), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                && amount > 0 && amount <= 9999999999999999.99m && decimal.Round(amount, 2) == amount) return amount;
            Error(segment, element, "Provide a positive invariant-decimal amount, up to 16 integer digits and two decimal places."); return 0;
        }
        private void Error(X12Segment segment, int element, string reason, int? component = null) => _errors.Add(new(
            segment.Id, segment.Position, element, reason, component,
            _transaction is null ? null : segment.Position - _transaction.Position + 1,
            _group?.Element(6), _transaction?.Element(2)));

        private sealed class Draft(X12Segment source, Claim claim, X12Segment? provider, X12Segment? subscriber, string group, string transaction)
        {
            public X12Segment Source { get; } = source;
            public Claim Claim { get; } = claim;
            public X12Segment? Provider { get; } = provider;
            public X12Segment? Subscriber { get; } = subscriber;
            public string GroupControl { get; } = group;
            public string TransactionControl { get; } = transaction;
            public List<string> Diagnoses { get; } = [];
            public List<(X12Segment Source, decimal Charge)> Services { get; } = [];
            public DateOnly? Date { get; set; }
            public X12Segment? DateSource { get; set; }
            public HashSet<int> DatedScopes { get; } = [];
        }
    }
}
