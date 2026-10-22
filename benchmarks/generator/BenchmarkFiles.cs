using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediBridge.Core.Rules;
using Microsoft.Data.Sqlite;

namespace MediBridge.Generator;

public static class BenchmarkFiles
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web);
    public static readonly string V1Json = JsonSerializer.Serialize(RuleCatalog.Baseline, RuleCatalog.Json);
    public static readonly string V2Json = new StreamReader(typeof(BenchmarkFiles).Assembly.GetManifestResourceStream("MediBridge.Benchmark.V2")!).ReadToEnd();
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string DatabasePath(string path)
    {
        // A filename only. No SQL connection string, URI, HTTP client, or Azure configuration is accepted.
        if (path.Contains(';') || path.Contains('=') || path.Contains("://") || !path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a local SQLite .db filename.");
        return Path.GetFullPath(path);
    }
    public static SqliteConnection Open(string path, bool create = false)
    {
        path = DatabasePath(path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open(); Execute(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"); return connection;
    }
    public static void Execute(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    { using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    public static long Count(SqliteConnection db, string table)
    {
        if (table is not ("Claims" or "Audit" or "Replay")) throw new ArgumentException("Unknown table.");
        using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT COUNT(*) FROM {table}"; return (long)cmd.ExecuteScalar()!;
    }
    public static SqliteCommand Insert(SqliteConnection db, string sql, params string[] names)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var name in names) cmd.Parameters.Add(new SqliteParameter(name, SqliteType.Text));
        cmd.Prepare(); return cmd;
    }
    public static void Values(SqliteCommand command, params object[] values)
    { for (var i = 0; i < values.Length; i++) command.Parameters[i].Value = values[i]; command.ExecuteNonQuery(); }
    public static object EnvironmentInfo() => new { runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = System.Environment.ProcessorCount,
        sqlite = typeof(SqliteConnection).Assembly.GetName().Version?.ToString(), buildConfiguration = BuildConfiguration };
#if DEBUG
    public const string BuildConfiguration = "Debug";
#else
    public const string BuildConfiguration = "Release";
#endif
    public static object SourceInfo(params string[] extraDirectories)
    {
        string Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit();
            return process.ExitCode == 0 ? output : "unknown";
        }
        var root = Git("rev-parse", "--show-toplevel");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = new List<string> { Path.Combine(root, "Directory.Build.props") };
        foreach (var directory in new[] { "src/MediBridge.Core", "benchmarks/generator", "benchmarks/shadow" }.Concat(extraDirectories).Distinct(StringComparer.Ordinal))
            files.AddRange(Directory.GetFiles(Path.Combine(root, directory), "*", SearchOption.AllDirectories)
                .Where(p => !p.Contains("/bin/") && !p.Contains("/obj/") && Path.GetExtension(p) is ".cs" or ".csproj" or ".json"));
        foreach (var path in files.Order(StringComparer.Ordinal))
        { hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path).Replace('\\', '/') + "\n")); hash.AppendData(File.ReadAllBytes(path)); }
        return new { baseCommit = Git("rev-parse", "HEAD"), contentSha256 = Convert.ToHexString(hash.GetHashAndReset()),
            scope = "Directory.Build.props, Core, generator and shadow sources including rule JSON; excludes bin/obj and result artifacts" + (extraDirectories.Length == 0 ? "" : "; additional source directories: " + string.Join(", ", extraDirectories)),
            scopedWorkingTree = Git(new[] { "status", "--porcelain", "--", "Directory.Build.props", "src/MediBridge.Core", "benchmarks/generator", "benchmarks/shadow" }.Concat(extraDirectories).ToArray()).Length == 0 ? "clean" : "modified" };
    }
    public static void Report(string output, object report, string harness, string runId, string status, string summary, string target = "Local SQLite / Core validation")
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, Json) + "\n");
        var ledger = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(output)!, "..", "RESULTS.md"));
        Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
        if (!File.Exists(ledger)) File.WriteAllText(ledger, "# Measured benchmark results\n\n| Completed UTC | Harness | Run ID | Status | Target | Measured result | Artifact |\n|---|---|---|---|---|---|---|\n");
        File.AppendAllText(ledger, $"| {DateTimeOffset.UtcNow:O} | {harness} | {runId} | {status} | {target} | {summary} | `results/{Path.GetFileName(output)}` |\n");
    }
    public static Dictionary<string, string> Options(string[] args, params string[] allowed)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Options require --name value pairs.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (!allowed.Contains(args[i], StringComparer.Ordinal) || !result.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown or repeated option: " + args[i]);
        return result;
    }
}
