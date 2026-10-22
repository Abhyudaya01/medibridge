using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediBridge.Core;
using MediBridge.Core.Rules;
using MediBridge.Generator;
using Microsoft.Data.Sqlite;

namespace MediBridge.Shadow;

public sealed class DetectionTally
{
    public int TruePositive { get; private set; }
    public int FalsePositive { get; private set; }
    public int FalseNegative { get; private set; }
    public int TrueNegative { get; private set; }
    public int ExpectedFlips => TruePositive + FalseNegative;
    public int DetectedFlips => TruePositive + FalsePositive;
    public double? DetectionPercent => ExpectedFlips == 0 ? null : 100.0 * TruePositive / ExpectedFlips;
    public double? PrecisionPercent => DetectedFlips == 0 ? null : 100.0 * TruePositive / DetectedFlips;
    public void Observe(bool expected, bool detected)
    {
        if (expected && detected) TruePositive++; else if (expected) FalseNegative++;
        else if (detected) FalsePositive++; else TrueNegative++;
    }
}

public static class ShadowProgram
{
    public static int Main(string[] args)
    {
        var output = "benchmarks/results/shadow.json"; var runId = Guid.NewGuid().ToString("N");
        try
        {
            var options = BenchmarkFiles.Options(args, "--database", "--output");
            output = options.GetValueOrDefault("--output", output);
            return Run(options.GetValueOrDefault("--database", "benchmarks/work/claims.db"), output, runId);
        }
        catch (Exception ex)
        {
            BenchmarkFiles.Report(output, new { status = "failed", runId, error = ex.Message }, "shadow", runId, "failed", "Replay did not complete; see artifact");
            Console.Error.WriteLine(ex.Message); return 1;
        }
    }
    public static int Run(string database, string output, string? runId = null)
    {
        runId ??= Guid.NewGuid().ToString("N"); var started = DateTimeOffset.UtcNow; var wall = Stopwatch.StartNew();
        using var db = BenchmarkFiles.Open(database);
        BenchmarkFiles.Execute(db, "PRAGMA synchronous=NORMAL;");
        using var metaCmd = db.CreateCommand(); metaCmd.CommandText = "SELECT Json FROM Metadata WHERE Id=1";
        using var manifest = JsonDocument.Parse((string)(metaCmd.ExecuteScalar() ?? throw new InvalidOperationException("Missing complete generator manifest.")));
        var meta = manifest.RootElement;
        if (meta.GetProperty("status").GetString() != "completed") throw new InvalidOperationException("Generation did not complete.");
        var expectedCount = meta.GetProperty("claimCount").GetInt32();
        var expectedCorpusHash = meta.GetProperty("corpusSha256").GetString();
        var definitions = new List<RuleDefinition>(); var ruleHashes = new List<string>();
        using (var rules = db.CreateCommand())
        {
            rules.CommandText = "SELECT Version,DefinitionJson,Sha256 FROM Rules ORDER BY CASE Version WHEN 'phase0-v1' THEN 0 ELSE 1 END";
            using var reader = rules.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(1); var hash = BenchmarkFiles.Hash(json);
                var expected = definitions.Count == 0 ? BenchmarkFiles.V1Json : BenchmarkFiles.V2Json;
                if (hash != reader.GetString(2) || hash != BenchmarkFiles.Hash(expected)) throw new InvalidOperationException("Stored rule definitions differ from the pinned benchmark contract.");
                var definition = RuleCatalog.Parse(json);
                if (definition.Version != reader.GetString(0)) throw new InvalidOperationException("Rule version mismatch.");
                definitions.Add(definition); ruleHashes.Add(hash);
            }
        }
        if (definitions.Count != 2) throw new InvalidOperationException("Expected exactly v1 and benchmark-v2.");
        var manifestRules = meta.GetProperty("rules").EnumerateArray().ToArray();
        if (manifestRules.Length != 2 || manifestRules.Where((r, i) => r.GetProperty("version").GetString() != definitions[i].Version
            || r.GetProperty("sha256").GetString() != ruleHashes[i]).Any())
            throw new InvalidOperationException("Generator manifest rule hashes differ from stored rules.");
        var tally = new DetectionTally(); var byPlan = new SortedDictionary<string, DetectionTally>(StringComparer.Ordinal);
        var replayed = 0; var labelMismatches = 0; var v1Matches = 0; var v2Matches = 0;
        var v1Accepted = 0; var v2Accepted = 0; var acceptedToRejected = 0; var rejectedToAccepted = 0;
        var mismatches = new List<object>(); var readTime = new Stopwatch(); var evaluateTime = new Stopwatch(); var writeTime = new Stopwatch();
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var result = BenchmarkFiles.Insert(db, "INSERT INTO Replay VALUES($run,$n,$v1,$v2,$e1,$e2,$f1,$f2)", "$run", "$n", "$v1", "$v2", "$e1", "$e2", "$f1", "$f2");
        using var audit = BenchmarkFiles.Insert(db, "INSERT INTO Audit(Id,Entity,EntityId,Outcome,Detail) VALUES($id,$entity,$key,$outcome,$detail)", "$id", "$entity", "$key", "$outcome", "$detail");
        while (true)
        {
            readTime.Start(); var batch = new List<(int Sequence, string Id, string Json, string InvalidKind)>();
            using (var select = db.CreateCommand())
            {
                select.CommandText = "SELECT Sequence,Id,ClaimJson,InvalidKind FROM Claims WHERE Sequence>$last ORDER BY Sequence LIMIT 1000";
                select.Parameters.AddWithValue("$last", replayed);
                using var reader = select.ExecuteReader();
                while (reader.Read()) batch.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
            readTime.Stop(); if (batch.Count == 0) break;
            evaluateTime.Start(); var observations = new List<(int Sequence, string Id, string V1, string V2, string E1, string E2, string F1, string F2)>(batch.Count);
            foreach (var row in batch)
            {
                if (row.Sequence != replayed + 1) throw new InvalidOperationException("Corpus sequence is not contiguous.");
                var claim = JsonSerializer.Deserialize<Claim>(row.Json, BenchmarkFiles.Compact) ?? throw new InvalidOperationException("Missing claim.");
                if (claim.Id != row.Id) throw new InvalidOperationException("Stored claim identity mismatch.");
                fingerprint.AppendData(Encoding.UTF8.GetBytes($"{row.Sequence}\t{row.InvalidKind}\t{row.Json}\n"));
                var e1 = GroundTruth.V1Accepts(claim) ? "Accepted" : "Rejected";
                var e2 = GroundTruth.V2Accepts(claim) ? "Accepted" : "Rejected";
                var input = RuleInput.From(claim);
                var live = RuleEvaluator.Evaluate(definitions[0], input); var shadow = RuleEvaluator.Evaluate(definitions[1], input);
                var flipped = live.Outcome != shadow.Outcome;
                tally.Observe(e1 != e2, flipped);
                if (!byPlan.TryGetValue(claim.PlanType, out var group)) byPlan[claim.PlanType] = group = new();
                group.Observe(e1 != e2, flipped);
                if (live.Outcome == e1) v1Matches++; if (shadow.Outcome == e2) v2Matches++;
                if (live.Outcome == "Accepted") v1Accepted++; if (shadow.Outcome == "Accepted") v2Accepted++;
                if (live.Outcome == "Accepted" && shadow.Outcome == "Rejected") acceptedToRejected++;
                if (live.Outcome == "Rejected" && shadow.Outcome == "Accepted") rejectedToAccepted++;
                var labelOk = (row.InvalidKind == "None") == (e1 == "Accepted"); if (!labelOk) labelMismatches++;
                if ((!labelOk || live.Outcome != e1 || shadow.Outcome != e2) && mismatches.Count < 20)
                    mismatches.Add(new { row.Sequence, claim.Id, row.InvalidKind, expectedV1 = e1, expectedV2 = e2, actualV1 = live, actualV2 = shadow });
                observations.Add((row.Sequence, claim.Id, live.Outcome, shadow.Outcome, e1, e2,
                    JsonSerializer.Serialize(live.Failures, BenchmarkFiles.Compact), JsonSerializer.Serialize(shadow.Failures, BenchmarkFiles.Compact)));
                replayed++;
            }
            evaluateTime.Stop(); writeTime.Start();
            using var tx = db.BeginTransaction(); result.Transaction = tx; audit.Transaction = tx;
            foreach (var item in observations)
            {
                BenchmarkFiles.Values(result, runId, item.Sequence, item.V1, item.V2, item.E1, item.E2, item.F1, item.F2);
                BenchmarkFiles.Values(audit, $"replay:{runId}:{item.Sequence}", "Replay", item.Id, item.V1 + "/" + item.V2,
                    $"run={runId}; expected={item.E1}/{item.E2}; v1={ruleHashes[0]}; v2={ruleHashes[1]}");
            }
            tx.Commit(); writeTime.Stop();
        }
        var actualHash = Convert.ToHexString(fingerprint.GetHashAndReset());
        var countMatches = replayed == expectedCount && BenchmarkFiles.Count(db, "Claims") == expectedCount;
        using var count = db.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM Replay WHERE RunId=$run"; count.Parameters.AddWithValue("$run", runId);
        var storedResults = (long)count.ExecuteScalar()!;
        var passed = countMatches && actualHash == expectedCorpusHash && storedResults == expectedCount && labelMismatches == 0
            && v1Matches == expectedCount && v2Matches == expectedCount && tally.FalsePositive == 0 && tally.FalseNegative == 0
            && tally.ExpectedFlips == meta.GetProperty("expectedFlips").GetInt32();
        var status = passed ? "completed" : "failed";
        var report = new { schemaVersion = 1, runId, status, startedAtUtc = started, completedAtUtc = DateTimeOffset.UtcNow,
            source = BenchmarkFiles.SourceInfo(), environment = BenchmarkFiles.EnvironmentInfo(), generatorRunId = meta.GetProperty("runId").GetString(),
            seed = meta.GetProperty("seed").GetUInt64(), expectedCount, replayedClaims = replayed, persistedObservations = storedResults,
            corpusSha256 = actualHash, corpusHashMatches = actualHash == expectedCorpusHash, labelMismatches,
            v1 = new { version = definitions[0].Version, sha256 = ruleHashes[0], accepted = v1Accepted, rejected = replayed - v1Accepted, oracleMatches = v1Matches },
            v2 = new { version = definitions[1].Version, sha256 = ruleHashes[1], accepted = v2Accepted, rejected = replayed - v2Accepted, oracleMatches = v2Matches },
            knownChange = "Add BENCH_PLUS_AMOUNT: reject SYN-PLUS amounts >100.00; all baseline rules unchanged. Previously invalid claims remain rejected.",
            groundTruth = "Independent fixed-contract validator and direct SYN-PLUS/amount predicate on each input; generated validity labels cross-checked. No evaluator calls in oracle.",
            tally.ExpectedFlips, flipCount = tally.DetectedFlips, tally.DetectionPercent, tally.PrecisionPercent, tally.TruePositive, tally.FalsePositive, tally.FalseNegative, tally.TrueNegative,
            acceptedToRejected, rejectedToAccepted, byPlan, mismatches,
            readSeconds = readTime.Elapsed.TotalSeconds, evaluationAndOracleSeconds = evaluateTime.Elapsed.TotalSeconds,
            persistSeconds = writeTime.Elapsed.TotalSeconds, wallSeconds = wall.Elapsed.TotalSeconds,
            measurement = "Local single-threaded replay of pinned Core rule definitions; <=1000-row batches; parsing, independent oracle and hashing included in evaluation phase. Wall time includes reads and atomic observation/audit persistence. Not API, Azure or outbox throughput." };
        using (var tx = db.BeginTransaction())
        {
            using var summary = BenchmarkFiles.Insert(db, "INSERT INTO Runs VALUES($run,$json)", "$run", "$json"); summary.Transaction = tx;
            BenchmarkFiles.Values(summary, runId, JsonSerializer.Serialize(report, BenchmarkFiles.Compact));
            audit.Transaction = tx; BenchmarkFiles.Values(audit, "run:" + runId, "Runs", runId, status, actualHash); tx.Commit();
        }
        BenchmarkFiles.Report(output, report, "shadow", runId, status,
            FormattableString.Invariant($"{replayed} claims; flips {tally.DetectedFlips}/{tally.ExpectedFlips}; detection {tally.DetectionPercent:F4}%; FP {tally.FalsePositive}; FN {tally.FalseNegative}; evaluation + oracle {report.evaluationAndOracleSeconds:F3} s; wall {report.wallSeconds:F3} s"));
        Console.WriteLine($"Shadow {status}: {replayed} claims, {tally.DetectedFlips}/{tally.ExpectedFlips} flips, detection {tally.DetectionPercent:F4}%, wall {report.wallSeconds:F3}s.");
        return passed ? 0 : 1;
    }
}
