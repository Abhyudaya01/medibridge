using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediBridge.Core.Rules;

namespace MediBridge.Generator;

public static class GeneratorProgram
{
    public static int Main(string[] args)
    {
        var output = "benchmarks/results/generator.json"; var runId = Guid.NewGuid().ToString("N");
        try
        {
            var options = BenchmarkFiles.Options(args, "--count", "--seed", "--database", "--output");
            output = options.GetValueOrDefault("--output", output);
            Run(int.Parse(options.GetValueOrDefault("--count", Corpus.DefaultCount.ToString())),
                ulong.Parse(options.GetValueOrDefault("--seed", Corpus.DefaultSeed.ToString())),
                options.GetValueOrDefault("--database", "benchmarks/work/claims.db"), output, runId); return 0;
        }
        catch (Exception ex)
        {
            BenchmarkFiles.Report(output, new { status = "failed", runId, error = ex.Message }, "generator", runId, "failed", "Generation did not complete; see artifact");
            Console.Error.WriteLine(ex.Message); return 1;
        }
    }
    public static object Run(int count, ulong seed, string database, string output, string? runId = null)
    {
        if (count is < 1 or > 1000000) throw new ArgumentOutOfRangeException(nameof(count));
        database = BenchmarkFiles.DatabasePath(database); runId ??= Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        // Refuse to replace a prior corpus or any application database.
        using (File.Open(database, FileMode.CreateNew, FileAccess.Write)) { }
        var started = DateTimeOffset.UtcNow; var total = Stopwatch.StartNew(); var generating = new Stopwatch(); var loading = new Stopwatch();
        var v1 = RuleCatalog.Parse(BenchmarkFiles.V1Json); var v2 = RuleCatalog.Parse(BenchmarkFiles.V2Json);
        using var db = BenchmarkFiles.Open(database, true);
        BenchmarkFiles.Execute(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        BenchmarkFiles.Execute(db, """
            CREATE TABLE Claims(Sequence INTEGER PRIMARY KEY, Id TEXT NOT NULL UNIQUE, ClaimJson TEXT NOT NULL, InvalidKind TEXT NOT NULL);
            CREATE TABLE Rules(Version TEXT PRIMARY KEY, DefinitionJson TEXT NOT NULL, Sha256 TEXT NOT NULL);
            CREATE TABLE Metadata(Id INTEGER PRIMARY KEY CHECK(Id=1), Json TEXT NOT NULL);
            CREATE TABLE Replay(RunId TEXT NOT NULL, Sequence INTEGER NOT NULL REFERENCES Claims(Sequence), V1Outcome TEXT NOT NULL,
                V2Outcome TEXT NOT NULL, ExpectedV1 TEXT NOT NULL, ExpectedV2 TEXT NOT NULL, V1Failures TEXT NOT NULL, V2Failures TEXT NOT NULL,
                PRIMARY KEY(RunId,Sequence));
            CREATE TABLE Runs(RunId TEXT PRIMARY KEY, SummaryJson TEXT NOT NULL);
            CREATE TABLE Audit(Id TEXT PRIMARY KEY, Entity TEXT NOT NULL, EntityId TEXT NOT NULL, Outcome TEXT NOT NULL, Detail TEXT NOT NULL, Timestamp TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')));
            """);
        foreach (var table in new[] { "Claims", "Rules", "Metadata", "Replay", "Runs", "Audit" })
            BenchmarkFiles.Execute(db, $"CREATE TRIGGER {table}_no_update BEFORE UPDATE ON {table} BEGIN SELECT RAISE(ABORT,'Append only'); END; CREATE TRIGGER {table}_no_delete BEFORE DELETE ON {table} BEGIN SELECT RAISE(ABORT,'Append only'); END;");
        using var insert = BenchmarkFiles.Insert(db, "INSERT INTO Claims VALUES($n,$id,$json,$kind)", "$n", "$id", "$json", "$kind");
        using var audit = BenchmarkFiles.Insert(db, "INSERT INTO Audit(Id,Entity,EntityId,Outcome,Detail) VALUES($id,$entity,$key,$outcome,$detail)", "$id", "$entity", "$key", "$outcome", "$detail");
        using (var tx = db.BeginTransaction())
        {
            audit.Transaction = tx;
            using var rule = BenchmarkFiles.Insert(db, "INSERT INTO Rules VALUES($v,$j,$h)", "$v", "$j", "$h"); rule.Transaction = tx;
            foreach (var pair in new[] { (v1.Version, BenchmarkFiles.V1Json), (v2.Version, BenchmarkFiles.V2Json) })
            {
                var hash = BenchmarkFiles.Hash(pair.Item2); BenchmarkFiles.Values(rule, pair.Version, pair.Item2, hash);
                BenchmarkFiles.Values(audit, "rule:" + pair.Version, "Rules", pair.Version, "Stored", hash);
            }
            tx.Commit();
        }
        var plans = new SortedDictionary<string, int>(StringComparer.Ordinal); var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var providers = new SortedDictionary<string, int>(StringComparer.Ordinal); var codes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        long amountCents = 0; decimal minimum = decimal.MaxValue, maximum = 0;
        var expectedFlips = 0; var loaded = 0; var labelMismatches = 0;
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var enumerator = Corpus.Generate(count, seed).GetEnumerator();
        void Increment(SortedDictionary<string, int> map, string key) => map[key] = map.GetValueOrDefault(key) + 1;
        while (loaded < count)
        {
            generating.Start(); var batch = new List<(CorpusRow Row, string Json)>(1000);
            while (batch.Count < 1000 && enumerator.MoveNext())
            {
                var row = enumerator.Current; var c = row.Claim;
                var json = JsonSerializer.Serialize(c, BenchmarkFiles.Compact);
                fingerprint.AppendData(Encoding.UTF8.GetBytes($"{row.Sequence}\t{row.InvalidKind}\t{json}\n"));
                Increment(plans, c.PlanType); Increment(kinds, row.InvalidKind); Increment(providers, c.ProviderNpi);
                foreach (var code in c.ProcedureCodes) Increment(codes, code);
                amountCents += (long)(c.Amount * 100); minimum = Math.Min(minimum, c.Amount); maximum = Math.Max(maximum, c.Amount);
                if (GroundTruth.Flips(c)) expectedFlips++;
                if (GroundTruth.V1Accepts(c) != (row.InvalidKind == "None")) labelMismatches++;
                batch.Add((row, json));
            }
            generating.Stop(); loading.Start();
            using var tx = db.BeginTransaction(); insert.Transaction = tx; audit.Transaction = tx;
            foreach (var item in batch)
            {
                BenchmarkFiles.Values(insert, item.Row.Sequence, item.Row.Claim.Id, item.Json, item.Row.InvalidKind);
                BenchmarkFiles.Values(audit, "claim:" + item.Row.Sequence, "Claims", item.Row.Claim.Id, "Loaded", BenchmarkFiles.Hash(item.Json));
            }
            tx.Commit(); loading.Stop(); loaded += batch.Count;
        }
        var actualRows = BenchmarkFiles.Count(db, "Claims"); var hashValue = Convert.ToHexString(fingerprint.GetHashAndReset());
        var status = actualRows == count && labelMismatches == 0 ? "completed" : "failed";
        var report = new { schemaVersion = 1, runId, status, startedAtUtc = started, completedAtUtc = DateTimeOffset.UtcNow,
            source = BenchmarkFiles.SourceInfo(), environment = BenchmarkFiles.EnvironmentInfo(), seed, claimCount = actualRows,
            corpusSha256 = hashValue, invalidClaims = count - kinds.GetValueOrDefault("None"), invalidPercent = 100.0 * (count - kinds.GetValueOrDefault("None")) / count,
            labelMismatches, expectedFlips, rules = new[] { new { version = v1.Version, sha256 = BenchmarkFiles.Hash(BenchmarkFiles.V1Json) }, new { version = v2.Version, sha256 = BenchmarkFiles.Hash(BenchmarkFiles.V2Json) } },
            distributions = new { plans, invalidKinds = kinds, providers, procedureCodes = codes, minimumAmount = minimum, maximumAmount = maximum, meanAmount = amountCents / (100m * count) },
            generationSeconds = generating.Elapsed.TotalSeconds, bulkLoadSeconds = loading.Elapsed.TotalSeconds, wallSeconds = total.Elapsed.TotalSeconds,
            storage = "Isolated benchmark schema; WAL, synchronous=NORMAL, prepared inserts, <=1000 rows/transaction. Claims and corresponding audits share a transaction; immutable tables have update/delete guards.",
            measurement = "Synthetic corpus construction and local SQLite bulk load, not API throughput or downstream processing. Distribution assumptions are documented, not clinical estimates." };
        using (var tx = db.BeginTransaction())
        {
            using var metadata = BenchmarkFiles.Insert(db, "INSERT INTO Metadata VALUES(1,$json)", "$json"); metadata.Transaction = tx;
            BenchmarkFiles.Values(metadata, JsonSerializer.Serialize(report, BenchmarkFiles.Compact));
            audit.Transaction = tx; BenchmarkFiles.Values(audit, "metadata:1", "Metadata", "1", status, hashValue); tx.Commit();
        }
        BenchmarkFiles.Report(output, report, "generator", runId, status,
            FormattableString.Invariant($"{actualRows} claims; invalid {count - kinds.GetValueOrDefault("None")} ({report.invalidPercent:F2}%); generation {report.generationSeconds:F3} s; bulk load {report.bulkLoadSeconds:F3} s; wall {report.wallSeconds:F3} s"));
        Console.WriteLine($"Generated {actualRows} claims; SHA256 {hashValue}; {status}.");
        if (status != "completed") throw new InvalidOperationException("Corpus row count or independent validity labels did not match.");
        return report;
    }
}
