using System.Text.Json;
using MediBridge.Core.Monitoring;
using MediBridge.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace MediBridge.Infrastructure.Monitoring;

public sealed class EndpointObservation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long CompletedAtTicks { get; set; }
    public string Method { get; set; } = "";
    public string Route { get; set; } = "";
    public int StatusCode { get; set; }
    public bool Exception { get; set; }
    public bool Aborted { get; set; }
    public bool Replayed { get; set; }
}
public sealed record MonitoringSeries(string Id, string Kind, string Label, string? Version, string? DefinitionHash,
    PChart Chart, int TotalBatches, int OmittedMiddleBatches);
public sealed record MonitoringReport(DateTimeOffset AsOf, int BatchMinutes, int BaselineBatches, int RecentBatches,
    long ExcludedHistoricalOrInvalidEvaluations, string EndpointDenominator, string RuleDenominator, IReadOnlyList<MonitoringSeries> Series);

public sealed class MonitoringStore(ClaimsDbContext db)
{
    public async Task<MonitoringReport> GetAsync(DateTimeOffset asOf, CancellationToken ct = default)
    {
        var cutoff = PCharts.StartTicks(asOf);
        var streams = new Dictionary<string, (string Kind, string Label, string? Version, string? Hash, ChartAccumulator Counts)>(StringComparer.Ordinal);
        ChartAccumulator Stream(string id, string kind, string label, string? version = null, string? hash = null)
        {
            if (!streams.TryGetValue(id, out var value)) streams.Add(id, value = (kind, label, version, hash, new()));
            return value.Counts;
        }
        // Database aggregation keeps HTTP volume out of application memory. UTC integer buckets work on both providers.
        var endpoints = db.EndpointObservations.AsNoTracking().Where(e => e.CompletedAtTicks < cutoff)
            .GroupBy(e => new { e.Method, e.Route, Batch = e.CompletedAtTicks / PCharts.BatchTicks })
            .Select(g => new { g.Key.Method, g.Key.Route, g.Key.Batch, N = g.Count(), D = g.Count(e => e.StatusCode >= 400 || e.Exception || e.Aborted) })
            .OrderBy(g => g.Batch).AsAsyncEnumerable();
        await foreach (var batch in endpoints.WithCancellation(ct))
            Stream("endpoint|" + batch.Method + "|" + batch.Route, "Endpoint", batch.Method + " " + batch.Route)
                .Add(batch.Batch * PCharts.BatchTicks, batch.N, batch.D);
        long excluded = 0;
        // Execution traces live inside existing immutable evaluation JSON, committed atomically with intake state.
        // Ordered streaming bounds memory to the calibration prefix and recent batches per series.
        var evaluations = db.RuleEvaluations.AsNoTracking().Where(e => e.ReceivedAtTicks < cutoff)
            .OrderBy(e => e.ReceivedAtTicks).ThenBy(e => e.Id)
            .Select(e => new { e.ReceivedAtTicks, e.Domain, e.LiveVersion, e.LiveHash, e.LiveJson, e.ProtocolAccepted }).AsAsyncEnumerable();
        await foreach (var row in evaluations.WithCancellation(ct))
        {
            RuleResult? result;
            try { result = JsonSerializer.Deserialize<RuleResult>(row.LiveJson); }
            catch (JsonException) { excluded++; continue; }
            if (!row.ProtocolAccepted || result is null || result.Error is not null || result.Version != row.LiveVersion
                || result.EvaluatedRuleIds is not { Count: > 0 } ids || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count
                || ids.Any(string.IsNullOrWhiteSpace) || result.Failures is null || result.Failures.Any(f => f is null || !ids.Contains(f.RuleId, StringComparer.Ordinal)))
            { excluded++; continue; }
            var failed = result.Failures.Select(f => f.RuleId).ToHashSet(StringComparer.Ordinal);
            foreach (var rule in ids)
                Stream($"rule|{row.Domain}|{row.LiveVersion}|{row.LiveHash}|{rule}", "Rule", row.Domain + " / " + rule, row.LiveVersion, row.LiveHash)
                    .Add(row.ReceivedAtTicks / PCharts.BatchTicks * PCharts.BatchTicks, 1, failed.Contains(rule) ? 1 : 0);
        }
        var series = streams.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s =>
        {
            var chart = s.Value.Counts.Complete();
            return new MonitoringSeries(s.Key, s.Value.Kind, s.Value.Label, s.Value.Version, s.Value.Hash, chart,
                s.Value.Counts.TotalBatches, s.Value.Counts.TotalBatches - chart.Points.Count);
        }).ToArray();
        return new(asOf, 5, PCharts.BaselineSize, 60, excluded,
            "Completed /api HTTP attempts, including replays; failures are status >=400, unhandled exceptions or client aborts. Health, static files and monitoring reads are excluded.",
            "One opportunity per actually executed live rule per committed, protocol-accepted intake evaluation. Conditional skips, shadow results, replays and legacy records without traces add no rule opportunities.", series);
    }
}
