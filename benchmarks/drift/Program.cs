using System.Diagnostics;
using MediBridge.Core.Monitoring;
using MediBridge.Generator;

namespace MediBridge.Drift;

public sealed record DriftTrial(int Number, ulong Seed, string Scenario, long BaselineObservations, long BaselineFailures,
    double? BaselineRate, int? BatchesUntilAlert, int MonitoringAlerts, int CalibrationOutsideLimits);

public static class DriftProgram
{
    public const ulong DefaultSeed = 3037837;
    public const int Training = 20, Monitoring = 40;
    public static BatchCount[] Generate(ulong seed, bool step)
    {
        var random = new SeededRandom(seed); var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, Training + Monitoring).Select(i =>
        {
            var n = 150 + random.Below(101); var threshold = step && i >= Training ? 100 : 50;
            var failures = Enumerable.Range(0, n).Count(_ => random.Below(1000) < threshold);
            return new BatchCount(start.AddMinutes(5 * i), n, failures);
        }).ToArray();
    }
    public static DriftTrial Measure(int number, ulong seed, bool step)
    {
        var chart = PCharts.Calculate(Generate(seed, step));
        var first = chart.Points.Skip(Training).Select((p, i) => new { p.Alert, Batch = i + 1 }).FirstOrDefault(p => p.Alert);
        return new(number, seed, step ? "Step" : "Stationary", chart.BaselineObservations, chart.BaselineFailures, chart.BaselineRate,
            first?.Batch, chart.Points.Count(p => p.Alert), chart.Points.Count(p => p.IsBaseline && p.OutsideLimits));
    }
    public static int Main(string[] args)
    {
        var output = "benchmarks/results/drift.json"; var id = Guid.NewGuid().ToString("N");
        try
        {
            var options = BenchmarkFiles.Options(args, "--output", "--trials", "--seed"); output = options.GetValueOrDefault("--output", output);
            Run(int.Parse(options.GetValueOrDefault("--trials", "100")), ulong.Parse(options.GetValueOrDefault("--seed", DefaultSeed.ToString())), output, id); return 0;
        }
        catch (Exception ex)
        {
            BenchmarkFiles.Report(output, new { status = "failed", runId = id, error = ex.Message }, "drift", id, "failed", "Simulation did not complete", "Local Core p-charts");
            Console.Error.WriteLine(ex.Message); return 1;
        }
    }
    public static object Run(int count, ulong seed, string output, string? runId = null)
    {
        if (count is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(count));
        runId ??= Guid.NewGuid().ToString("N"); var started = DateTimeOffset.UtcNow; var watch = Stopwatch.StartNew();
        var random = new SeededRandom(seed); var shifts = new List<DriftTrial>(); var controls = new List<DriftTrial>();
        for (var i = 0; i < count; i++)
        {
            var trialSeed = random.Next();
            shifts.Add(Measure(i + 1, trialSeed, true)); controls.Add(Measure(i + 1, trialSeed, false));
        }
        var delays = shifts.Where(t => t.BatchesUntilAlert.HasValue).Select(t => t.BatchesUntilAlert!.Value).Order().ToArray();
        double? median = delays.Length == 0 ? null : delays.Length % 2 == 1 ? delays[delays.Length / 2] : (delays[delays.Length / 2 - 1] + delays[delays.Length / 2]) / 2.0;
        int? p95 = delays.Length == 0 ? null : delays[(int)Math.Ceiling(.95 * delays.Length) - 1];
        var report = new { schemaVersion = 1, runId, status = "completed", startedAtUtc = started, completedAtUtc = DateTimeOffset.UtcNow,
            source = BenchmarkFiles.SourceInfo("benchmarks/drift"), environment = BenchmarkFiles.EnvironmentInfo(), seed,
            trialsPerScenario = count, baselineBatches = Training, monitoringBatches = Monitoring, batchMinutes = 5,
            baselineFailureProbability = .05, stepFailureProbability = .10, observationsPerBatch = "Uniform integer 150–250; independent Bernoulli failures.",
            detectedTrials = delays.Length, missedTrials = count - delays.Length, detectionPercent = 100.0 * delays.Length / count,
            meanBatchesUntilAlert = delays.Length == 0 ? (double?)null : delays.Average(), medianBatchesUntilAlert = median, p95BatchesUntilAlert = p95,
            stationaryTrialsWithFalseAlerts = controls.Count(t => t.BatchesUntilAlert.HasValue), stationaryAlertPoints = controls.Sum(t => t.MonitoringAlerts),
            stationaryMonitoringPoints = count * Monitoring, wallSeconds = watch.Elapsed.TotalSeconds,
            delayDefinition = "First post-change batch = 1. No alert within 40 monitored batches is censored, not zero delay. Delay summaries include detected trials only. Stationary controls retain 5% throughout; false alerts are reported, not forced to zero.",
            measurement = "Seeded local Core p-chart simulation; same failure stream can represent failed rule executions or HTTP 422 attempts. No HTTP calls, storage throughput or Azure load is measured. Baseline is estimated once from the first 20 nonempty batches and not refit after the step.",
            firstTrial = shifts[0], exampleChart = PCharts.Calculate(Generate(shifts[0].Seed, true)), shiftedTrials = shifts, stationaryTrials = controls };
        BenchmarkFiles.Report(output, report, "drift", runId, "completed",
            FormattableString.Invariant($"{delays.Length}/{count} step trials alerted; first trial {shifts[0].BatchesUntilAlert} batch(es); median {median:F2}, p95 {p95} batches; stationary false-alert trials {report.stationaryTrialsWithFalseAlerts}/{count}; wall {report.wallSeconds:F3} s"), "Local Core p-charts / seeded simulation");
        Console.WriteLine($"Drift: {delays.Length}/{count} detected; median {median:F2}, p95 {p95} batches; stationary false-alert trials {report.stationaryTrialsWithFalseAlerts}/{count}.");
        return report;
    }
}
