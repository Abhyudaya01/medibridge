using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediBridge.Core.PriorAuth;
using MediBridge.Infrastructure.PriorAuth;

namespace MediBridge.Parity;

public static class ParityProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--samples")
        {
            Directory.CreateDirectory(args[1]);
            var submitted = new DateTimeOffset(2026, 1, 20, 12, 0, 0, TimeSpan.Zero);
            await File.WriteAllTextAsync(Path.Combine(args[1], "pa-x12-278.278"), PairedRequests.X12("PA-SYN-SAMPLE", "SYN-PA-SAMPLE", "0000000006", ["SYN-SERVICE-A", "SYN-SERVICE-B"], "U", submitted) + "\n");
            await File.WriteAllTextAsync(Path.Combine(args[1], "pa-fhir.json"), PairedRequests.Fhir("PA-SYN-SAMPLE", "SYN-PA-SAMPLE", "0000000006", ["SYN-SERVICE-A", "SYN-SERVICE-B"], "U", submitted, true) + "\n");
            return 0;
        }
        if (args.Length != 0 && !(args.Length == 2 && args[0] == "--output")) { Console.Error.WriteLine("Usage: dotnet run --project benchmarks/parity -- [--output path.json]"); return 2; }
        var output = Path.GetFullPath(args.Length == 2 ? args[1] : "benchmarks/results/parity.json");
        var ledger = Path.Combine(Path.GetDirectoryName(output)!, "..", "RESULTS.md");
        var started = DateTime.UtcNow; var watch = Stopwatch.StartNew(); var cases = PairedRequests.Generate();
        var rows = cases.Select(pair =>
        {
            var x12 = X12278Mapper.Map(pair.X12); var fhir = FhirPriorAuthMapper.Map(pair.Fhir);
            var sameModel = PriorAuthRules.Equivalent(x12.Request, fhir.Request);
            var expectedModels = PriorAuthRules.Equivalent(x12.Request, pair.Expected) && PriorAuthRules.Equivalent(fhir.Request, pair.Expected);
            var xRules = x12.Errors.Select(e => e.RuleId).Distinct().Order(StringComparer.Ordinal).ToArray();
            var fRules = fhir.Errors.Select(e => e.RuleId).Distinct().Order(StringComparer.Ordinal).ToArray();
            var sameOutcome = x12.Outcome == fhir.Outcome && xRules.SequenceEqual(fRules);
            var expectedOutcomes = x12.Outcome == pair.ExpectedOutcome && fhir.Outcome == pair.ExpectedOutcome && xRules.SequenceEqual(pair.ExpectedRules) && fRules.SequenceEqual(pair.ExpectedRules);
            return new { pair.Number, pair.Variant, canonicalEqual = sameModel, outcomeEqual = sameOutcome, matchesExpectedModel = expectedModels, matchesExpectedOutcome = expectedOutcomes,
                passed = sameModel && sameOutcome && expectedModels && expectedOutcomes,
                expected = pair.Expected, expectedOutcome = pair.ExpectedOutcome, expectedRules = pair.ExpectedRules, x12, fhir,
                x12Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.X12))), fhirSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Fhir))) };
        }).ToArray();
        string Git(params string[] command)
        {
            var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in command) info.ArgumentList.Add(arg);
            using var child = Process.Start(info)!; var value = child.StandardOutput.ReadToEnd().Trim(); child.WaitForExit(); return child.ExitCode == 0 ? value : "unknown";
        }
        string SourceHash()
        {
            var root = Git("rev-parse", "--show-toplevel");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var folder in new[] { "src", "benchmarks/parity" })
            foreach (var path in Directory.GetFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories).Where(p => !p.Contains("/bin/") && !p.Contains("/obj/"))
                .Where(p => Path.GetExtension(p) is ".cs" or ".csproj").Order(StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path).Replace('\\', '/') + "\n")); hash.AppendData(File.ReadAllBytes(path));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        var passed = rows.Count(r => r.passed); var rate = 100.0 * passed / rows.Length;
        var report = new { schemaVersion = "1", startedAtUtc = started, completedAtUtc = DateTime.UtcNow,
            sourceCommit = Git("rev-parse", "HEAD"), sourceContentSha256 = SourceHash(), sourceTree = Git("status", "--porcelain").Length == 0 ? "clean" : "dirty",
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, fhirSdk = typeof(Hl7.Fhir.Model.Claim).Assembly.GetName().Version?.ToString(),
            measurement = "Canonical mapping and shared intake validation only; no authorization, coverage or medical-necessity decisions.",
            totalPairs = rows.Length, acceptedCases = cases.Count(c => c.ExpectedOutcome == "Accepted"), rejectedCases = cases.Count(c => c.ExpectedOutcome == "Rejected"),
            canonicalMatches = rows.Count(r => r.canonicalEqual), outcomeMatches = rows.Count(r => r.outcomeEqual), expectedMatches = rows.Count(r => r.matchesExpectedModel && r.matchesExpectedOutcome),
            passedPairs = passed, parityPercent = rate, wallSeconds = watch.Elapsed.TotalSeconds, status = passed == rows.Length ? "completed" : "failed", cases = rows };
        var options = new JsonSerializerOptions(PriorAuthJson.Options) { WriteIndented = true };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!); await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, options) + "\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ledger))!);
        if (!File.Exists(ledger)) await File.WriteAllTextAsync(ledger, "# Measured benchmark results\n\n| Completed UTC | Harness | Run ID | Status | Target | Measured result | Artifact |\n|---|---|---|---|---|---|---|\n");
        await File.AppendAllTextAsync(ledger, FormattableString.Invariant($"| {report.completedAtUtc:O} | parity | {report.startedAtUtc:yyyyMMddTHHmmssZ} | {report.status} | Local X12 278 / FHIR R4 | {passed}/{rows.Length} paired cases passed ({rate:F4}%); canonical {report.canonicalMatches}; outcomes {report.outcomeMatches}; independent expected {report.expectedMatches} | `results/{Path.GetFileName(output)}` |\n"));
        Console.WriteLine($"Parity: {passed}/{rows.Length} ({rate:F4}%). Artifact: {output}");
        if (passed != rows.Length) Console.WriteLine(JsonSerializer.Serialize(rows.Where(r => !r.passed).Take(3), options));
        return passed == rows.Length ? 0 : 1;
    }
}
