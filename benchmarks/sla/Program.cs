using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediBridge.Core.PriorAuth;
using MediBridge.Infrastructure;
using MediBridge.Infrastructure.PriorAuth;
using Microsoft.EntityFrameworkCore;

namespace MediBridge.Sla;

public static class SlaProgram
{
    public static async Task<int> Main(string[] args)
    {
        var count = 10000; var seed = 278837; var output = "benchmarks/results/sla.json";
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) return Usage();
            if (args[i] == "--count" && int.TryParse(args[i + 1], out var n) && n is >= 1 and <= 100000) count = n;
            else if (args[i] == "--seed" && int.TryParse(args[i + 1], out var s)) seed = s;
            else if (args[i] == "--output") output = args[i + 1];
            else return Usage();
        }
        return await RunAsync(count, seed, Path.GetFullPath(output));
    }
    private static int Usage() { Console.Error.WriteLine("Usage: dotnet run --project benchmarks/sla -- [--count 10000] [--seed 278837] [--output benchmarks/results/sla.json]"); return 2; }
    public static async Task<int> RunAsync(int count, int seed, string output)
    {
        var started = DateTimeOffset.UtcNow; var watch = Stopwatch.StartNew();
        var path = Path.Combine(Path.GetTempPath(), "medibridge-sla-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var clock = new SimulationClock { UtcNow = Scenario.Start };
            var plans = Scenario.Generate(count, seed); var schedule = new SortedDictionary<DateTimeOffset, List<(PlannedRequest Plan, string Action)>>();
            void Schedule(PlannedRequest plan, DateTimeOffset at, string action)
            {
                var tick = Scenario.Tick(at); if (!schedule.TryGetValue(tick, out var items)) schedule[tick] = items = [];
                items.Add((plan, action));
            }
            foreach (var plan in plans)
            {
                Schedule(plan, plan.Receipt, "Arrive");
                if (plan.PendingAt is { } pending) Schedule(plan, pending, "Pended");
                Schedule(plan, plan.DecisionAt, plan.Decision);
            }
            var options = new DbContextOptionsBuilder<SqliteClaimsDbContext>().UseSqlite($"Data Source={path}").Options;
            await using var db = new SqliteClaimsDbContext(options); await db.InitializeAsync();
            var store = new PriorAuthLifecycleStore(db, clock);
            var drainEnd = schedule.Keys.Last(); var day30 = Scenario.Start.AddDays(30); var scans = 0;
            for (var tick = Scenario.Start; tick <= drainEnd; tick = tick.AddMinutes(1))
            {
                clock.UtcNow = tick; schedule.TryGetValue(tick, out var actions); actions ??= [];
                // Arrival timestamps are exact; polling and review commits occur on the next minute.
                foreach (var (plan, _) in actions.Where(a => a.Action == "Arrive"))
                {
                    var canonical = new PriorAuthRequest(plan.Id, "SYN-" + plan.Id, "0000000006", ["SYN-SERVICE-A"], plan.Priority, plan.Receipt.AddMinutes(-5));
                    var result = await new PriorAuthStore(db).SubmitAsync(new(canonical, PriorAuthRules.Validate(canonical)), "SLA-SIM", plan.Receipt);
                    if (result?.Outcome != "Accepted") throw new InvalidOperationException("Synthetic benchmark intake failed.");
                }
                // Same processor as the BackgroundService, including persistence, audit and concurrency protection.
                while (await store.SweepAsync() > 0) { }
                scans++;
                foreach (var (plan, action) in actions.Where(a => a.Action != "Arrive"))
                {
                    var result = await store.DecideAsync(plan.Id, new(action, action == "Denied" ? plan.Reason : null));
                    if (result.StatusCode != 200) throw new InvalidOperationException("Synthetic scheduled decision failed: " + result.Error);
                }
                db.ChangeTracker.Clear();
                if (count >= 1000 && tick.TimeOfDay == TimeSpan.Zero && (tick - Scenario.Start).Days % 5 == 0)
                    Console.WriteLine($"Simulated day {(tick - Scenario.Start).Days}; elapsed {watch.Elapsed.TotalSeconds:F1}s.");
            }
            var events = await db.PriorAuthEvents.AsNoTracking().Where(e => e.StateJson != null).OrderBy(e => e.Id).ToListAsync();
            var grouped = events.GroupBy(e => e.RequestId).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            var rows = plans.OrderBy(p => p.Id, StringComparer.Ordinal).Select(plan =>
            {
                var audit = grouped[plan.Id]; var finalEvent = audit.Last(e => e.Stage == "Decision");
                var state = JsonSerializer.Deserialize<PriorAuthLifecycle>(finalEvent.StateJson!, PriorAuthJson.Options)!;
                var risk = audit.Where(e => e.Stage == "Escalation" && e.Outcome == "AtRisk").ToArray();
                var breach = audit.Where(e => e.Stage == "Escalation" && e.Outcome == "Breached").ToArray();
                var expectedDue = Scenario.Due(plan); var finalAt = Scenario.Tick(plan.DecisionAt);
                var missed = state.DecidedAt >= expectedDue;
                var correct = state.ReceivedAt == plan.Receipt && state.DueAt == expectedDue && state.DecidedAt == finalAt && state.Decision.ToString() == plan.Decision
                    && state.ReasonCode?.ToString() == plan.Reason && audit.Count(e => e.Stage == "Decision" && e.Outcome != "Pended") == 1
                    && risk.Length == (finalAt >= Scenario.Risk(plan) ? 1 : 0) && breach.Length == (finalAt >= expectedDue ? 1 : 0)
                    && risk.All(e => e.Timestamp == Scenario.Tick(Scenario.Risk(plan))) && breach.All(e => e.Timestamp == Scenario.Tick(expectedDue));
                return new { plan.Id, plan.Priority, receivedAt = plan.Receipt, dueAt = expectedDue, pendingAt = plan.PendingAt, finalAt = state.DecidedAt,
                    decision = state.Decision, reasonCode = state.ReasonCode, missedDeadline = missed, atRiskEscalations = risk.Length, breachEscalations = breach.Length,
                    atRiskLeadHours = risk.Length == 0 ? (double?)null : (expectedDue - risk[0].Timestamp).TotalHours, passed = correct };
            }).ToArray();
            var leads = rows.Where(r => r.atRiskLeadHours.HasValue).Select(r => r.atRiskLeadHours!.Value).Order().ToArray();
            var snapshot30 = events.Where(e => e.Timestamp < day30).GroupBy(e => e.RequestId)
                .Select(g => JsonSerializer.Deserialize<PriorAuthLifecycle>(g.Last().StateJson!, PriorAuthJson.Options)!).ToArray();
            var metrics = PriorAuthMetricMath.Compute(events.Select(e => new PriorAuthMetricObservation(e.RequestId, JsonSerializer.Deserialize<PriorAuthLifecycle>(e.StateJson!, PriorAuthJson.Options)!)));
            var passed = rows.All(r => r.passed) && rows.Length == count && metrics[0].Finalized == count;
            var report = new { schemaVersion = "1", runId = started.ToString("yyyyMMddTHHmmssZ"), startedAtUtc = started, completedAtUtc = DateTimeOffset.UtcNow,
                sourceCommit = Git("rev-parse", "HEAD"), sourceContentSha256 = SourceHash(), runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                measurement = "Synthetic SLA correctness and virtual escalation timing through the production SQLite lifecycle store; not real processing throughput or clinical outcomes.",
                seed, requestCount = count, arrivalWindowDays = 30, simulatedStart = Scenario.Start, arrivalCutoff = day30, drainedUntil = drainEnd, scanIntervalSeconds = 60, scanCount = scans,
                distribution = new { expeditedProbability = .15, pendingProbability = .20, approvalProbability = .75, businessHourWeight = 1, weekdayOffHourWeight = .2, weekendWeight = .15,
                    standardMedianBaselineHours = 48, expeditedMedianBaselineHours = 20, lognormalSigma = .8, baselineClampsHours = "standard 1–480; expedited 1–240", pendingDelayHours = "standard uniform 48–144; expedited uniform 12–48" },
                atDay30 = new { received = snapshot30.Length, unresolved = snapshot30.Count(s => !s.IsFinal), breachedUnresolved = snapshot30.Count(s => !s.IsFinal && s.Sla == PriorAuthSla.Breached) },
                missedDeadlines = rows.Count(r => r.missedDeadline), escalationsFired = rows.Sum(r => r.atRiskEscalations + r.breachEscalations), atRiskEscalations = rows.Sum(r => r.atRiskEscalations), breachEscalations = rows.Sum(r => r.breachEscalations),
                escalationLeadTimeHours = new { count = leads.Length, mean = leads.Length == 0 ? (double?)null : leads.Average(), median = leads.Length == 0 ? (double?)null : (leads[(leads.Length - 1) / 2] + leads[leads.Length / 2]) / 2,
                    min = leads.Length == 0 ? (double?)null : leads.Min(), max = leads.Length == 0 ? (double?)null : leads.Max() },
                byPriority = rows.GroupBy(r => r.Priority).Select(g => new { priority = g.Key, count = g.Count(), missedDeadlines = g.Count(r => r.missedDeadline), atRiskEscalations = g.Sum(r => r.atRiskEscalations), breachEscalations = g.Sum(r => r.breachEscalations), meanAtRiskLeadHours = g.Average(r => r.atRiskLeadHours) }).ToArray(),
                metrics, independentOracleMatches = rows.Count(r => r.passed), status = passed ? "completed" : "failed", wallSeconds = watch.Elapsed.TotalSeconds, requests = rows };
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions(PriorAuthJson.Options) { WriteIndented = true }) + "\n");
            var ledger = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(output)!, "..", "RESULTS.md"));
            if (!File.Exists(ledger)) await File.WriteAllTextAsync(ledger, "# Measured benchmark results\n\n| Completed UTC | Harness | Run ID | Status | Target | Measured result | Artifact |\n|---|---|---|---|---|---|---|\n");
            await File.AppendAllTextAsync(ledger, FormattableString.Invariant($"| {report.completedAtUtc:O} | sla | {report.runId} | {report.status} | SQLite / 30-day accelerated arrivals | {count} requests; missed deadlines {report.missedDeadlines}; escalations {report.escalationsFired} (at risk {report.atRiskEscalations}, breached {report.breachEscalations}); at-risk mean/median lead {report.escalationLeadTimeHours.mean:F4}/{report.escalationLeadTimeHours.median:F4} h; oracle {report.independentOracleMatches}/{count} | `results/{Path.GetFileName(output)}` |\n"));
            Console.WriteLine($"SLA: {count} requests; {report.missedDeadlines} missed; {report.escalationsFired} escalations; oracle {report.independentOracleMatches}/{count}; {report.status}. Artifact: {output}");
            return passed ? 0 : 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
        }
    }
    private static string Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; var result = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); return process.ExitCode == 0 ? result : "unknown";
    }
    private static string SourceHash()
    {
        var root = Git("rev-parse", "--show-toplevel"); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var folder in new[] { "src", "benchmarks/sla" })
        foreach (var path in Directory.GetFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains("/bin/") && !p.Contains("/obj/") && Path.GetExtension(p) is ".cs" or ".csproj").Order(StringComparer.Ordinal))
        { hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path).Replace('\\', '/') + "\n")); hash.AppendData(File.ReadAllBytes(path)); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
