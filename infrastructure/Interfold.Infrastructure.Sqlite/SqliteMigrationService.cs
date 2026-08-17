using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Interfold.Shared.Contracts.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Sqlite;

/// <summary>Applies embedded SQL migrations at startup. Ledger is
/// <c>schema_migrations</c> keyed on version + SHA-256 checksum.</summary>
public sealed class SqliteMigrationService(
    IOptions<PersistenceConfiguration> options,
    ILogger<SqliteMigrationService> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) =>
        MigrateAsync(options.Value.SqliteConnectionString, logger, cancellationToken);

    /// <summary>Idempotent migration entry point for fixtures / bootstrap.</summary>
    public static async Task MigrateAsync(
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogInformation("[sqlite-migrate] No connection string — skipping migrations.");
            return;
        }

        logger.LogInformation("[sqlite-migrate] Applying SQLite schema migrations...");

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureLedgerAsync(connection, cancellationToken);
        var applied = await LoadAppliedAsync(connection, cancellationToken);

        var migrations = GetMigrationScripts();
        var appliedBy = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var newlyApplied = 0;
        var skipped = 0;

        foreach (var (name, sql) in migrations)
        {
            var checksum = ComputeChecksum(sql);

            if (applied.TryGetValue(name, out var existingChecksum))
            {
                if (!string.Equals(existingChecksum, checksum, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"[sqlite-migrate] Checksum mismatch for migration '{name}': " +
                        $"recorded={existingChecksum}, actual={checksum}. Migration files must not be " +
                        "edited after they have been applied.");
                }

                logger.LogDebug("[sqlite-migrate] Skipping {Migration}, already applied.", name);
                skipped++;
                continue;
            }

            logger.LogInformation("[sqlite-migrate] Applying: {Migration}", name);
            var stopwatch = Stopwatch.StartNew();

            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                foreach (var statement in SplitStatements(sql))
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = statement;
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                stopwatch.Stop();

                await using (var insertCmd = connection.CreateCommand())
                {
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = """
                        INSERT INTO schema_migrations
                            (version, checksum, duration_ms, applied_by, applied_at)
                        VALUES ($version, $checksum, $duration_ms, $applied_by, $applied_at)
                        """;
                    insertCmd.Parameters.AddWithValue("$version", name);
                    insertCmd.Parameters.AddWithValue("$checksum", checksum);
                    insertCmd.Parameters.AddWithValue("$duration_ms", (int)stopwatch.ElapsedMilliseconds);
                    insertCmd.Parameters.AddWithValue("$applied_by", appliedBy);
                    insertCmd.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
                newlyApplied++;
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }

        logger.LogInformation(
            "[sqlite-migrate] Applied {New} new migration(s), skipped {Skipped} already-applied.",
            newlyApplied, skipped);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static List<(string Name, string Sql)> GetMigrationScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = "Interfold.Infrastructure.Sqlite.Migrations.";

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (Name: n[prefix.Length..], Sql: reader.ReadToEnd());
            })
            .ToList();
    }

    private static async Task EnsureLedgerAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version      TEXT    PRIMARY KEY,
                checksum     TEXT    NOT NULL,
                applied_at   INTEGER NOT NULL,
                duration_ms  INTEGER NOT NULL,
                applied_by   TEXT
            );
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, string>> LoadAppliedAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version, checksum FROM schema_migrations";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied[reader.GetString(0)] = reader.GetString(1);
        }
        return applied;
    }

    public static string ComputeChecksum(string sql)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    private static IEnumerable<string> SplitStatements(string sql)
    {
        // Strip -- line comments before splitting on ';' so comment text like
        // "wire values; booleans as INTEGER" cannot create bogus statements.
        var withoutComments = string.Join(
            '\n',
            sql.Split('\n').Select(line =>
            {
                var idx = line.IndexOf("--", StringComparison.Ordinal);
                return idx < 0 ? line : line[..idx];
            }));

        foreach (var part in withoutComments.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            yield return part;
        }
    }
}
