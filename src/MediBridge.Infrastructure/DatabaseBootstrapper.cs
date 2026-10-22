using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.Sqlite;

namespace MediBridge.Infrastructure;

public static class DatabaseBootstrapper
{
    public static async Task InitializeAsync(ClaimsDbContext db, CancellationToken cancellationToken)
    {
        var creator = db.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken)) await creator.CreateAsync(cancellationToken);
        // Never blindly mark an arbitrary pre-migrations database as migrated.
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!(await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any())
            {
                var claims = await Columns("Claims");
                var events = await Columns("ClaimEvents");
                if (claims.Count > 0 || events.Count > 0)
                {
                    string[] expectedClaims = ["Id", "ProviderNpi", "MemberId", "ServiceDate", "ProcedureCodes", "Amount", "Status", "FailureCategory", "RuleVersion", "ValidationFailures", "DuplicateKey"];
                    string[] expectedEvents = ["Id", "ClaimId", "Timestamp", "Stage", "Outcome", "Detail"];
                    if (!claims.SetEquals(expectedClaims) || !events.SetEquals(expectedEvents))
                        throw new InvalidOperationException("Unrecognized legacy schema; refusing migration baseline adoption.");
                    if (db.Database.GetDbConnection() is SqliteConnection sqlite && sqlite.DataSource != ":memory:")
                    {
                        await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlite.DataSource + ".legacy-backup.db" }.ToString());
                        await backup.OpenAsync(cancellationToken); sqlite.BackupDatabase(backup);
                    }
                    var history = db.GetService<IHistoryRepository>();
                    await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), cancellationToken);
                    var baseline = db.Database.GetMigrations().First();
                    await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(baseline, "10.0.13")), cancellationToken);
                }
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
        await db.Database.MigrateAsync(cancellationToken);
        var legacyRejected = await db.Claims.Where(c => c.Status == MediBridge.Core.ClaimStatus.Rejected
            && c.FailureCategory == MediBridge.Core.FailureCategory.BusinessRule && !db.Corrections.Any(q => q.ClaimId == c.Id)).ToListAsync(cancellationToken);
        foreach (var claim in legacyRejected) ReliableWrites.Track(db, claim, DateTime.UtcNow);
        if (legacyRejected.Count > 0) await db.SaveChangesAsync(cancellationToken);
        foreach (var table in new[] { "ClaimEvents", "Audit", "PriorAuthEvents", "RuleSets", "RuleActivations", "RuleEvaluations" })
        {
            if (db.Database.IsSqlite())
            {
                var noUpdate = $"CREATE TRIGGER IF NOT EXISTS {table}_NoUpdate BEFORE UPDATE ON {table} BEGIN SELECT RAISE(ABORT, 'Append-only audit'); END;";
                await db.Database.ExecuteSqlRawAsync(noUpdate, cancellationToken);
                var noDelete = $"CREATE TRIGGER IF NOT EXISTS {table}_NoDelete BEFORE DELETE ON {table} BEGIN SELECT RAISE(ABORT, 'Append-only audit'); END;";
                await db.Database.ExecuteSqlRawAsync(noDelete, cancellationToken);
            }
            else
            {
                var appendOnly = $"CREATE OR ALTER TRIGGER {table}_AppendOnly ON {table} INSTEAD OF UPDATE, DELETE AS BEGIN THROW 51000, 'Append-only audit', 1; END;";
                await db.Database.ExecuteSqlRawAsync(appendOnly, cancellationToken);
            }
        }

        await Rules.RuleRegistry.SeedAsync(db, cancellationToken);

        async Task<HashSet<string>> Columns(string table)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = db.Database.IsSqlite() ? $"SELECT name FROM pragma_table_info('{table}')" : $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table}'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(0));
            return columns;
        }
    }
}
