namespace MediBridge.Core.X12;

public sealed record X12Delimiters(char Element, char Component, char Repetition, char Segment)
{
    public static X12Delimiters Fallback { get; } = new('*', ':', '^', '~');
}

public sealed record X12Error(string SegmentId, int SegmentPosition, int ElementPosition, string Reason,
    int? ComponentPosition = null, int? TransactionPosition = null,
    string? GroupControlNumber = null, string? TransactionControlNumber = null);

public sealed record X12Segment(string Id, int Position, string[] Elements)
{
    public string Element(int position) => position > 0 && position <= Elements.Length ? Elements[position - 1] : "";
}

public sealed record X12Tokens(X12Delimiters Delimiters, IReadOnlyList<X12Segment> Segments, IReadOnlyList<X12Error> Errors);
public sealed record X12Transaction(string GroupControlNumber, string ControlNumber, int SegmentPosition);
public sealed record X12ParsedClaim(Claim Claim, IReadOnlyList<string> DiagnosisCodes, int SegmentPosition,
    string GroupControlNumber, string TransactionControlNumber);
public sealed record X12Interchange(string? ControlNumber, X12Delimiters Delimiters,
    IReadOnlyList<X12Transaction> Transactions, IReadOnlyList<X12ParsedClaim> Claims, IReadOnlyList<X12Error> Errors)
{
    public bool Accepted => Errors.Count == 0;
}
public sealed record X12Acknowledgment(string Status, string? InterchangeControlNumber, string Text, IReadOnlyList<X12Error> Errors);
