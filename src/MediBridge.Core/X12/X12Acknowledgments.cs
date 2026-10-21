using System.Globalization;

namespace MediBridge.Core.X12;

public static class X12Acknowledgments
{
    // Simulation format: one ST/SE 999-style block per input group, wrapped by the JSON interchange result.
    // No production sender/receiver envelope, timestamps, or external trading partner is invented.
    public static X12Acknowledgment Generate(X12Interchange interchange)
    {
        var d = interchange.Delimiters;
        List<string> output = [];
        var groups = interchange.Transactions.Select(t => t.GroupControlNumber)
            .Concat(interchange.Errors.Where(e => e.GroupControlNumber is not null).Select(e => e.GroupControlNumber!)).Distinct().ToList();
        if (groups.Count == 0) groups.Add("UNKNOWN");
        var number = 0;
        foreach (var group in groups)
        {
            var control = (++number).ToString("D4", CultureInfo.InvariantCulture);
            List<string> segments = [];
            void Add(params string[] fields) => segments.Add(string.Join(d.Element, fields));
            Add("ST", "999", control, "005010X231A1");
            Add("AK1", "HC", Safe(group));
            var transactions = interchange.Transactions.Where(t => t.GroupControlNumber == group).ToList();
            foreach (var transaction in transactions)
            {
                Add("AK2", "837", Safe(transaction.ControlNumber));
                var errors = interchange.Errors.Where(e => e.TransactionControlNumber == transaction.ControlNumber && e.GroupControlNumber == group).ToList();
                foreach (var error in errors)
                {
                    Add("IK3", Safe(error.SegmentId), (error.TransactionPosition ?? error.SegmentPosition).ToString(CultureInfo.InvariantCulture), "", error.ElementPosition == 0 ? "7" : "8");
                    if (error.ElementPosition > 0)
                    {
                        var position = error.ElementPosition.ToString(CultureInfo.InvariantCulture);
                        if (error.ComponentPosition is not null) position += d.Component + error.ComponentPosition.Value.ToString(CultureInfo.InvariantCulture);
                        Add("IK4", position, "", "7");
                    }
                }
                var rejected = !interchange.Accepted;
                Add("IK5", rejected ? "R" : "A");
            }
            foreach (var error in interchange.Errors.Where(e => e.TransactionControlNumber is null
                && (e.GroupControlNumber == group || (e.GroupControlNumber is null && group == groups[0]))))
                {
                    Add("IK3", Safe(error.SegmentId), error.SegmentPosition.ToString(CultureInfo.InvariantCulture), "", error.ElementPosition == 0 ? "7" : "8");
                    if (error.ElementPosition > 0) Add("IK4", error.ElementPosition.ToString(CultureInfo.InvariantCulture), "", "7");
                }
            var groupRejected = !interchange.Accepted;
            var accepted = groupRejected ? 0 : transactions.Count;
            Add("AK9", groupRejected ? "R" : "A", transactions.Count.ToString(CultureInfo.InvariantCulture), transactions.Count.ToString(CultureInfo.InvariantCulture), accepted.ToString(CultureInfo.InvariantCulture));
            Add("SE", (segments.Count + 1).ToString(CultureInfo.InvariantCulture), control);
            output.AddRange(segments);
        }
        return new(interchange.Accepted ? "Accepted" : "Rejected", interchange.ControlNumber,
            string.Join(d.Segment, output) + d.Segment, interchange.Errors);
    }

    private static string Safe(string value) =>
        new(value.Select(c => char.IsAsciiLetterOrDigit(c) ? c : 'X').ToArray());
}
