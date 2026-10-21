using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediBridge.Core;
using MediBridge.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediBridge.Chaos;

public sealed class BenchmarkClock(DateTime start) : TimeProvider
{
    private DateTime _now = start;
    public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
    public void AdvanceTo(DateTime value) { if (value > _now) _now = value; }
}

public sealed class ChaosFaults(int seed, IReadOnlyDictionary<string, string> identities, string? crashStep, int crashOrdinal) : IFaultInjector
{
    private int _seen;
    public static bool Inject(int seed, string claimId, int attempt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{claimId}:{attempt}"));
        // Deterministic independent per-claim/attempt draw; probability 0.2 (not a fixed success count).
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) / ((double)uint.MaxValue + 1) < 0.2;
    }
    public Task<FailureDescriptor?> BeforeAttemptAsync(string messageId, int attempt, CancellationToken cancellationToken) =>
        Task.FromResult<FailureDescriptor?>(Inject(seed, identities[messageId], attempt) ? new("Timeout", "Chaos harness injected transient timeout.") : null);
    public async Task CheckpointAsync(string step, CancellationToken cancellationToken)
    {
        if (step != crashStep || ++_seen != crashOrdinal) return;
        Console.WriteLine("KILL:" + step); await Console.Out.FlushAsync(cancellationToken);
        // Parent kills this real process while it is paused at the selected production checkpoint.
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}

public static class ChaosMeasurements
{
    public static (int Rejected, int Retried, int Attempts) BusinessRules(IEnumerable<Claim> claims, IEnumerable<ClaimEvent> events)
    {
        // Follow the original immutable rejection event even if erroneous processing later clears the category.
        var rejected = events.Where(e => e.Stage == "Validation" && e.Outcome == "Rejected").Select(e => e.ClaimId).ToHashSet();
        var business = claims.Where(c => rejected.Contains(c.Id)).ToList();
        return (business.Count, business.Count(c => c.AttemptCount > 0), business.Sum(c => c.AttemptCount));
    }
}

public sealed record Observation(int Iteration, string CrashStep, int CrashOrdinal, int CrashExitCode,
    int Claims, int InvalidClaims, int DuplicateClaims, int DuplicateHandlerEffects, int ReplaySuppressed,
    int ProcessingAttempts, int TransientFailureEvents, int TransientAffectedClaims, int TransientRecoveredClaims,
    int BusinessRuleFailuresRetried, int BusinessRuleProcessingAttempts, int Succeeded, int ManualReview, int PendingOutbox,
    int Receipts, double WallSeconds);

public sealed record ChaosTotals(int Iterations, int Claims, int InvalidClaims, int DuplicateClaims, int DuplicateHandlerEffects,
    int ReplaySuppressed, int ProcessingAttempts, int TransientFailureEvents, int TransientAffectedClaims,
    int TransientRecoveredClaims, double? TransientRecoveryPercent, int BusinessRuleFailuresRetried,
    int BusinessRuleProcessingAttempts, int Succeeded, int ManualReview, int PendingOutbox, int Receipts)
{
    public static ChaosTotals From(IReadOnlyList<Observation> rows)
    {
        var affected = rows.Sum(r => r.TransientAffectedClaims); var recovered = rows.Sum(r => r.TransientRecoveredClaims);
        return new(rows.Count, rows.Sum(r => r.Claims), rows.Sum(r => r.InvalidClaims), rows.Sum(r => r.DuplicateClaims),
            rows.Sum(r => r.DuplicateHandlerEffects), rows.Sum(r => r.ReplaySuppressed), rows.Sum(r => r.ProcessingAttempts),
            rows.Sum(r => r.TransientFailureEvents), affected, recovered, affected == 0 ? null : 100.0 * recovered / affected,
            rows.Sum(r => r.BusinessRuleFailuresRetried), rows.Sum(r => r.BusinessRuleProcessingAttempts),
            rows.Sum(r => r.Succeeded), rows.Sum(r => r.ManualReview), rows.Sum(r => r.PendingOutbox), rows.Sum(r => r.Receipts));
    }
}

public sealed record ChaosReport(string SchemaVersion, string RunId, DateTime StartedAtUtc, DateTime CompletedAtUtc,
    string SourceCommit, string SourceTree, string SourceContentSha256, string Runtime, string Os, string Architecture, int ProcessorCount,
    int Seed, int RequestedIterations, int Parallelism, int BatchSize, double InvalidRate, double TransientFailureRate, string Clock, double WallSeconds,
    string Status, string? Error, ChaosTotals Totals, IReadOnlyList<Observation> Observations);

public static class ChaosProgram
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "--worker") return await Worker(args[1], int.Parse(args[2]), args[3] == "none" ? null : args[3], int.Parse(args[4]));
            if (args.Contains("--help")) { Console.WriteLine("dotnet run --project benchmarks/chaos -c Release -- --iterations 1000 --seed 1701 --parallelism 4 --output benchmarks/results/chaos.json --results benchmarks/RESULTS.md"); return 0; }
            var allowed = new[] { "--iterations", "--seed", "--parallelism", "--output", "--results" };
            if (args.Length % 2 != 0 || args.Where((_, i) => i % 2 == 0).Any(a => !allowed.Contains(a))) throw new ArgumentException("Unknown or incomplete option; use --help.");
            string Option(string name, string fallback) { var index = Array.IndexOf(args, name); return index < 0 ? fallback : args[index + 1]; }
            int iterations = int.Parse(Option("--iterations", "1000")), seed = int.Parse(Option("--seed", "1701"));
            int parallelism = int.Parse(Option("--parallelism", Math.Min(4, Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture)));
            if (parallelism is < 1 or > 16) throw new ArgumentException("Parallelism must be 1–16.");
            if (iterations is < 1 or > 100000) throw new ArgumentException("Iterations must be 1–100000.");
            var output = Path.GetFullPath(Option("--output", "benchmarks/results/chaos.json"));
            var results = Path.GetFullPath(Option("--results", "benchmarks/RESULTS.md"));
            await RunBenchmark(iterations, seed, output, results, parallelism); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    public static async Task<ChaosReport> RunBenchmark(int iterations, int seed, string output, string results, int parallelism = 1)
    {
        var started = DateTime.UtcNow; var watch = Stopwatch.StartNew(); var completed = new ConcurrentDictionary<int, Observation>();
        var runId = started.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var work = Path.Combine(Path.GetTempPath(), "medibridge-chaos-" + runId); Directory.CreateDirectory(work);
        string status = "completed"; string? error = null;
        try
        {
            var random = new Random(seed);
            var plans = Enumerable.Range(1, iterations).Select(i => (Iteration: i,
                Step: new[] { "AfterLease", "AfterAttemptStarted", "AfterHandlerCommit" }[random.Next(3)],
                Ordinal: random.Next(1, 11), DataSeed: random.Next())).ToArray();
            await Parallel.ForEachAsync(plans, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, async (plan, cancellation) =>
            {
                var path = Path.Combine(work, $"iteration-{plan.Iteration}.db");
                var row = await Iteration(path, seed, plan.Iteration, plan.Step, plan.Ordinal, new Random(plan.DataSeed));
                completed[row.Iteration] = row;
                if (row.DuplicateClaims != 0 || row.DuplicateHandlerEffects != 0 || row.BusinessRuleProcessingAttempts != 0 || row.PendingOutbox != 0
                    || row.Claims != 50 || row.InvalidClaims != 5 || row.ReplaySuppressed != 50 || row.Receipts != row.Succeeded)
                    throw new InvalidOperationException($"Integrity invariant failed in iteration {row.Iteration}.");
                var count = completed.Count;
                if (count == 1 || count % 25 == 0 || count == iterations) Console.WriteLine($"Chaos {count}/{iterations}; elapsed {watch.Elapsed.TotalSeconds:F1}s; transient-affected {completed.Values.Sum(r => r.TransientAffectedClaims)}; duplicates {completed.Values.Sum(r => r.DuplicateClaims)}");
                foreach (var file in Directory.GetFiles(work, $"iteration-{plan.Iteration}.db*")) File.Delete(file);
            });
        }
        catch (Exception exception) { status = "failed"; error = exception.Message; }
        var rows = completed.Values.OrderBy(row => row.Iteration).ToList();
        var report = new ChaosReport("1", runId, started, DateTime.UtcNow, Git("rev-parse", "HEAD"), Git("status", "--porcelain", "--untracked-files=normal").Length == 0 ? "clean" : "dirty", SourceContentHash(),
            RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            seed, iterations, parallelism, 50, 0.1, 0.2, "virtual UTC; real child-process kills and SQLite persistence; lease/retry deadlines advanced", watch.Elapsed.TotalSeconds,
            status, error, ChaosTotals.From(rows), rows);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, Json) + "\n");
        var totals = report.Totals;
        await ResultsLedger.AppendAsync(results, $"| {report.CompletedAtUtc:O} | chaos | {runId} | {status} | SQLite / virtual clock | {totals.Iterations} iterations; {totals.Claims} claims; duplicates {totals.DuplicateClaims}; duplicate effects {totals.DuplicateHandlerEffects}; transient recovery {totals.TransientRecoveredClaims}/{totals.TransientAffectedClaims} ({totals.TransientRecoveryPercent?.ToString("F4", CultureInfo.InvariantCulture) ?? "n/a"}%); business-rule retried {totals.BusinessRuleFailuresRetried} | `{Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(results))!, output).Replace('\\', '/')}` |");
        if (status == "failed") throw new InvalidOperationException($"Chaos benchmark failed; partial measured results saved to {output}. {error}");
        Directory.Delete(work, true);
        return report;
    }

    private static string SourceContentHash()
    {
        var root = Git("rev-parse", "--show-toplevel");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var folder in new[] { "src", "benchmarks/chaos" })
        foreach (var path in Directory.GetFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj").Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path).Replace('\\', '/') + "\n"));
            hash.AppendData(File.ReadAllBytes(path));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var child = Process.Start(info)!; var text = child.StandardOutput.ReadToEnd().Trim(); child.WaitForExit();
        return child.ExitCode == 0 ? text : "dirty/unknown";
    }

    private static ServiceProvider Services(string path, TimeProvider clock, IFaultInjector? faults = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<SqliteClaimsDbContext>(options => options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()));
        services.AddScoped<ClaimsDbContext>(s => s.GetRequiredService<SqliteClaimsDbContext>());
        services.AddSingleton<ClaimWriteLock>(); services.AddScoped<ClaimStore>(); services.AddSingleton(clock);
        if (faults is not null) { services.AddSingleton(faults); services.AddSingleton<OutboxDispatcher>(); }
        return services.BuildServiceProvider();
    }

    private static async Task<Observation> Iteration(string path, int seed, int iteration, string step, int ordinal, Random random)
    {
        var watch = Stopwatch.StartNew();
        await using var services = Services(path, TimeProvider.System);
        var invalid = Enumerable.Range(0, 50).OrderBy(_ => random.Next()).Take(5).ToHashSet();
        Claim Make(int n) => new() { Id = $"CLM-CHAOS-{seed}-{iteration}-{n}", ProviderNpi = invalid.Contains(n) ? "" : "0000000006",
            MemberId = $"SYN-CHAOS-{seed}-{iteration}-{n}", ServiceDate = new(2026, 1, 20), ProcedureCodes = ["SYN-CHAOS"], Amount = 10m };
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>(); await db.InitializeAsync();
            var store = scope.ServiceProvider.GetRequiredService<ClaimStore>();
            for (int n = 0; n < 50; n++) await store.SubmitAsync(Make(n), Simulation.None, DateTime.UtcNow);
        }
        var exitCode = await RunChild(path, seed, step, ordinal);
        int suppressed = 0;
        using (var scope = services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ClaimStore>();
            for (int n = 0; n < 50; n++) if (await store.SubmitAsync(Make(n), Simulation.None, DateTime.UtcNow) is null) suppressed++;
        }
        await RunChild(path, seed, null, 0);
        using var check = services.CreateScope(); var saved = check.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        var claims = await saved.Claims.AsNoTracking().ToListAsync(); var events = await saved.ClaimEvents.AsNoTracking().ToListAsync();
        var failures = events.Where(e => e.Outcome == "AttemptFailed" && e.Detail.StartsWith("Transient;", StringComparison.Ordinal)).ToList();
        var affected = failures.Select(e => e.ClaimId).ToHashSet();
        var business = ChaosMeasurements.BusinessRules(claims, events);
        return new(iteration, step, ordinal, exitCode, claims.Count, business.Rejected,
            claims.Where(c => c.Status != ClaimStatus.Rejected && c.Status != ClaimStatus.Suppressed).GroupBy(ClaimRules.DuplicateKey).Sum(g => Math.Max(0, g.Count() - 1)),
            events.Where(e => e.Outcome == "AttemptSucceeded").GroupBy(e => e.ClaimId).Sum(g => Math.Max(0, g.Count() - 1)), suppressed,
            claims.Sum(c => c.AttemptCount), failures.Count, affected.Count, claims.Count(c => affected.Contains(c.Id) && c.Status == ClaimStatus.Succeeded),
            business.Retried, business.Attempts, claims.Count(c => c.Status == ClaimStatus.Succeeded),
            claims.Count(c => c.Status == ClaimStatus.ManualReview), await saved.Outbox.CountAsync(m => m.State != "Completed" && m.State != "DeadLetter"),
            await saved.Receipts.CountAsync(), watch.Elapsed.TotalSeconds);
    }

    private static async Task<int> RunChild(string path, int seed, string? crashStep, int ordinal)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { typeof(ChaosProgram).Assembly.Location, "--worker", path, seed.ToString(CultureInfo.InvariantCulture), crashStep ?? "none", ordinal.ToString(CultureInfo.InvariantCulture) }) info.ArgumentList.Add(arg);
        info.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        bool killed = false;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line != "KILL:" + crashStep) continue;
                process.Kill(entireProcessTree: true); killed = true; break;
            }
            await process.WaitForExitAsync(timeout.Token);
            if (crashStep is not null && !killed) throw new InvalidOperationException("Processing host exited without reaching selected crash checkpoint. " + await stderr);
            if (crashStep is null && process.ExitCode != 0) throw new InvalidOperationException("Restarted host failed: " + await stderr);
            return process.ExitCode;
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    private static async Task<int> Worker(string path, int seed, string? step, int ordinal)
    {
        // All startup timestamps and persisted leases use UTC. Jump over the killed host's lease, never rewrite the DB clock fields.
        var clock = new BenchmarkClock(DateTime.UtcNow.AddMinutes(5));
        Dictionary<string, string> identities;
        await using (var lookup = Services(path, clock))
        {
            using var scope = lookup.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
            identities = await db.Outbox.ToDictionaryAsync(m => m.Id, m => m.ClaimId);
        }
        await using var services = Services(path, clock, new ChaosFaults(seed, identities, step, ordinal));
        var dispatcher = services.GetRequiredService<OutboxDispatcher>();
        for (int deliveries = 0; deliveries < 1000; deliveries++)
        {
            if (await dispatcher.DispatchOneAsync()) continue;
            using var scope = services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
            var remaining = await db.Outbox.AsNoTracking().Where(m => m.State == "Pending" || m.State == "Processing" || m.State == "AwaitingAck").ToListAsync();
            if (remaining.Count == 0) return 0;
            clock.AdvanceTo(remaining.Min(m => m.State == "Pending" ? m.NextAttemptAt : m.LeaseUntil!.Value).AddTicks(1));
        }
        throw new InvalidOperationException("Dispatcher exceeded bounded drain limit.");
    }
}

public static class ResultsLedger
{
    public static async Task AppendAsync(string path, string row)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!File.Exists(path)) await File.WriteAllTextAsync(path, "# Measured benchmark results\n\nSynthetic data only. Chaos uses a virtual clock; load measures HTTP submission latency, not end-to-end recovery.\n\n| Completed UTC | Harness | Run ID | Status | Target | Measured result | Artifact |\n|---|---|---|---|---|---|---|\n");
        await File.AppendAllTextAsync(path, row + "\n");
    }
}
