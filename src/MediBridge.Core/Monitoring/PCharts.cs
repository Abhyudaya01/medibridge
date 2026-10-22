namespace MediBridge.Core.Monitoring;

public sealed record BatchCount(DateTimeOffset Start, int Observations, int Failures);
public sealed record PChartPoint(DateTimeOffset Start, int Observations, int Failures, double Rate,
    bool IsBaseline, double? Center, double? Lower, double? Upper, bool OutsideLimits, bool Alert, bool? NormalApproximationReliable);
public sealed record PChart(string Status, int RequiredBaselineBatches, int BaselineBatches, long BaselineObservations,
    long BaselineFailures, double? BaselineRate, IReadOnlyList<PChartPoint> Points);

public static class PCharts
{
    public const int BaselineSize = 20;
    public const long BatchTicks = TimeSpan.TicksPerMinute * 5;
    public static long StartTicks(DateTimeOffset timestamp) => timestamp.UtcTicks / BatchTicks * BatchTicks;
    public static PChart Calculate(IEnumerable<BatchCount> batches, int baselineSize = BaselineSize)
    {
        if (baselineSize is < 2 or > 100) throw new ArgumentOutOfRangeException(nameof(baselineSize));
        var all = batches.OrderBy(b => b.Start).ToArray();
        if (all.Any(b => b.Start.Offset != TimeSpan.Zero || b.Observations < 0 || b.Failures < 0 || b.Failures > b.Observations)
            || all.Select(b => b.Start).Distinct().Count() != all.Length) throw new ArgumentException("Use unique UTC batches with 0 <= failures <= observations.");
        var nonempty = all.Where(b => b.Observations > 0).ToArray();
        var training = nonempty.Take(baselineSize).ToArray();
        var n = training.Sum(b => (long)b.Observations); var d = training.Sum(b => (long)b.Failures);
        var ready = training.Length == baselineSize; double? center = ready ? (double)d / n : null;
        var points = nonempty.Select((b, index) =>
        {
            double? margin = center.HasValue ? 3 * Math.Sqrt(center.Value * (1 - center.Value) / b.Observations) : null;
            double? lower = margin.HasValue ? Math.Max(0, center!.Value - margin.Value) : null;
            double? upper = margin.HasValue ? Math.Min(1, center!.Value + margin.Value) : null;
            var rate = (double)b.Failures / b.Observations;
            var outside = center.HasValue && (rate < lower || rate > upper);
            bool? reliable = center.HasValue ? b.Observations * center.Value >= 5 && b.Observations * (1 - center.Value) >= 5 : null;
            return new PChartPoint(b.Start, b.Observations, b.Failures, rate, index < baselineSize, center, lower, upper, outside, index >= baselineSize && outside, reliable);
        }).ToArray();
        return new(ready ? "Ready" : "InsufficientBaseline", baselineSize, training.Length, n, d, center, Array.AsReadOnly(points));
    }
}

// Streams must arrive in timestamp order. Keep the original calibration and only the latest display batches.
// Middle batches are counted but never allowed to replace the calibration prefix.
public sealed class ChartAccumulator(int displayBatches = 60)
{
    private readonly List<BatchCount> baseline = [];
    private readonly Queue<BatchCount> recent = [];
    private long? current;
    private int observations, failures;
    public int TotalBatches { get; private set; }
    public void Add(long startTicks, int n, int d)
    {
        if (displayBatches is < 1 or > 500 || n < 1 || d < 0 || d > n || startTicks % PCharts.BatchTicks != 0)
            throw new ArgumentException("Invalid monitoring batch.");
        if (current.HasValue && startTicks < current) throw new ArgumentException("Stream must be chronological.");
        if (current != startTicks)
        {
            Flush(); current = startTicks; observations = 0; failures = 0;
        }
        observations = checked(observations + n); failures = checked(failures + d);
    }
    private void Flush()
    {
        if (!current.HasValue) return;
        var batch = new BatchCount(new DateTimeOffset(current.Value, TimeSpan.Zero), observations, failures);
        if (baseline.Count < PCharts.BaselineSize) baseline.Add(batch);
        else { recent.Enqueue(batch); if (recent.Count > displayBatches) recent.Dequeue(); }
        TotalBatches++; current = null;
    }
    public PChart Complete() { Flush(); return PCharts.Calculate(baseline.Concat(recent)); }
}
