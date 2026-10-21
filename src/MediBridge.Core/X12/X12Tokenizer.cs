namespace MediBridge.Core.X12;

public static class X12Tokenizer
{
    private static readonly int[] Separators = [3, 6, 17, 20, 31, 34, 50, 53, 69, 76, 81, 83, 89, 99, 101, 103];

    public static IReadOnlyList<X12Tokens> Tokenize(string input)
    {
        List<X12Tokens> results = [];
        var cursor = 0;
        if (input.StartsWith('\uFEFF')) cursor++;
        while (cursor < input.Length)
        {
            SkipLines();
            if (cursor == input.Length) break;
            var start = cursor;
            List<X12Error> errors = [];
            List<X12Segment> segments = [];
            var delimiters = X12Delimiters.Fallback;
            if (input.Length - start < 106 || !input.AsSpan(start).StartsWith("ISA"))
            {
                RejectHeader("Expected a complete fixed-width 106-character ISA header.");
                continue;
            }
            delimiters = new(input[start + 3], input[start + 104], input[start + 82], input[start + 105]);
            var declared = new[] { delimiters.Element, delimiters.Component, delimiters.Repetition, delimiters.Segment };
            if (declared.Distinct().Count() != 4 || declared.Any(c => c < '!' || c > '~' || char.IsLetterOrDigit(c)))
            {
                RejectHeader("ISA delimiters must be four distinct printable ASCII punctuation characters.");
                continue;
            }
            var badSeparator = Separators.FirstOrDefault(offset => input[start + offset] != delimiters.Element, -1);
            if (badSeparator >= 0)
            {
                RejectHeader($"ISA fixed-width element separator is missing at character {badSeparator + 1}.", Array.IndexOf(Separators, badSeparator));
                continue;
            }
            if (input.AsSpan(start, 106).IndexOfAnyInRange((char)0, (char)31) >= 0 || input.AsSpan(start, 106).IndexOfAnyInRange((char)127, char.MaxValue) >= 0)
            {
                RejectHeader("ISA must contain printable ASCII characters without embedded line breaks.");
                continue;
            }
            segments.Add(Segment(input.Substring(start, 105), 1, delimiters.Element));
            cursor += 106;
            var ended = false;
            while (cursor < input.Length)
            {
                SkipLines();
                if (cursor == input.Length) break;
                // A new ISA at a segment boundary must not be consumed by an unclosed interchange.
                if (input.AsSpan(cursor).StartsWith("ISA")) break;
                var end = input.IndexOf(delimiters.Segment, cursor);
                if (end < 0)
                {
                    var raw = input[cursor..].TrimEnd('\r', '\n');
                    var segment = Segment(raw, segments.Count + 1, delimiters.Element);
                    segments.Add(segment);
                    errors.Add(new(segment.Id, segment.Position, 0, "Missing declared segment terminator."));
                    cursor = input.Length;
                    break;
                }
                var current = Segment(input[cursor..end], segments.Count + 1, delimiters.Element);
                segments.Add(current);
                if (input.AsSpan(cursor, end - cursor).ContainsAny('\r', '\n') || input.AsSpan(cursor, end - cursor).IndexOfAnyInRange((char)0, (char)31) >= 0
                    || input.AsSpan(cursor, end - cursor).IndexOfAnyInRange((char)127, char.MaxValue) >= 0)
                    errors.Add(new(current.Id, current.Position, 0, "Segments must be printable ASCII; CR/LF is permitted only between segments."));
                cursor = end + 1;
                if (current.Id == "IEA") { ended = true; break; }
            }
            if (!ended) errors.Add(new("IEA", segments.Count + 1, 0, "Missing interchange trailer before end of input or the next ISA."));
            results.Add(new(delimiters, segments, errors));

            void RejectHeader(string reason, int element = 0)
            {
                errors.Add(new("ISA", 1, Math.Max(0, element), reason));
                results.Add(new(X12Delimiters.Fallback, [], errors));
                // Header recovery is deliberately restricted to an ISA after a physical line boundary.
                var next = input.IndexOf("\nISA", start + 1, StringComparison.Ordinal);
                cursor = next < 0 ? input.Length : next + 1;
            }
        }
        if (results.Count == 0)
            results.Add(new(X12Delimiters.Fallback, [], [new("ISA", 1, 0, "The interchange is empty; provide a synthetic ISA envelope.")]));
        return results;

        void SkipLines() { while (cursor < input.Length && input[cursor] is '\r' or '\n') cursor++; }
    }

    private static X12Segment Segment(string raw, int position, char separator)
    {
        var parts = raw.Split(separator, StringSplitOptions.None);
        return new(parts[0], position, parts.Skip(1).ToArray());
    }
}
