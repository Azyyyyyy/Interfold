using Interfold.Shared.Contracts.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Sqlite;

public interface ISqliteConnectionFactory
{
    Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Opens a SQLite connection and applies the locked-in pragmas (WAL, NORMAL sync,
/// foreign keys, busy timeout). Repositories consume this factory.
/// </summary>
public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(IOptions<PersistenceConfiguration> options)
        : this(options.Value.SqliteConnectionString)
    {
    }

    public SqliteConnectionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        string[] pragmas =
        [
            "PRAGMA journal_mode=WAL;",
            "PRAGMA synchronous=NORMAL;",
            "PRAGMA foreign_keys=ON;",
            "PRAGMA busy_timeout=5000;",
        ];

        foreach (var pragma in pragmas)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = pragma;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
