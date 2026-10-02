using Interfold.Shared.Domain.Abstractions;
using Microsoft.Data.Sqlite;

namespace Interfold.Infrastructure.Sqlite;

public sealed class SqliteStorageTransactionFactory : IStorageTransactionFactory
{
    private readonly ISqliteConnectionFactory _connections;

    public SqliteStorageTransactionFactory(ISqliteConnectionFactory connections)
    {
        _connections = connections;
    }

    public StorageTransactionStart BeginAsync(CancellationToken cancellationToken = default)
        => new(new ValueTask<IStorageTransaction>(SqliteStorageTransaction.BeginAsync(_connections, cancellationToken)));
}

internal sealed class SqliteStorageTransaction : IStorageTransaction
{
    private static readonly AsyncLocal<SqliteStorageTransaction?> Ambient = new();

    internal static SqliteStorageTransaction? Current => Ambient.Value;

    internal SqliteConnection Connection { get; }
    internal SqliteTransaction Transaction { get; }

    private SqliteStorageTransaction? _previous;
    private bool _joined;
    private bool _committed;
    private bool _rollbackRequired;
    private bool _disposed;

    private SqliteStorageTransaction(SqliteConnection connection, SqliteTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    internal void RequireRollback() => _rollbackRequired = true;

    public static async Task<IStorageTransaction> BeginAsync(
        ISqliteConnectionFactory connections,
        CancellationToken cancellationToken)
    {
        if (Current is { _disposed: false } ambient)
            return new SqliteDependentTransaction(ambient);

        var connection = await connections.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            return new SqliteStorageTransaction(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public void Join()
    {
        if (_joined)
            return;

        _joined = true;
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
            return;

        if (_rollbackRequired)
            throw new InvalidOperationException("Storage transaction can no longer be committed.");

        await Transaction.CommitAsync(cancellationToken);
        _committed = true;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        // Synchronous so the AsyncLocal restore stays on the caller's flow. An async
        // dispose puts the write back when the state machine yields.
        if (_joined && Ambient.Value == this)
            Ambient.Value = _previous;

        if (!_committed)
        {
            try
            {
                Transaction.Rollback();
            }
            catch (InvalidOperationException)
            {
                // Commit or an earlier rollback already completed the transaction.
            }
        }

        Transaction.Dispose();
        Connection.Dispose();
        return ValueTask.CompletedTask;
    }
}

// Shares the ambient connection. Commit is deferred to the outer scope so a nested
// delete cannot commit a prefix of a larger handler transaction.
internal sealed class SqliteDependentTransaction : IStorageTransaction
{
    private readonly SqliteStorageTransaction _outer;
    private bool _completed;
    private bool _disposed;

    public SqliteDependentTransaction(SqliteStorageTransaction outer) => _outer = outer;

    public void Join()
    {
    }

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        _completed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        if (!_completed)
            _outer.RequireRollback();

        return ValueTask.CompletedTask;
    }
}

internal sealed class SqliteWork : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly bool _owns;
    private bool _committed;
    private bool _disposed;

    public SqliteConnection Connection => _connection;
    public SqliteTransaction Transaction => _transaction;

    private SqliteWork(SqliteConnection connection, SqliteTransaction transaction, bool owns)
    {
        _connection = connection;
        _transaction = transaction;
        _owns = owns;
    }

    public static async Task<SqliteWork> OpenAsync(ISqliteConnectionFactory connections, CancellationToken cancellationToken)
    {
        var ambient = SqliteStorageTransaction.Current;
        if (ambient is not null)
            return new SqliteWork(ambient.Connection, ambient.Transaction, owns: false);

        var connection = await connections.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            return new SqliteWork(connection, transaction, owns: true);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (!_owns || _committed || _disposed)
            return;

        await _transaction.CommitAsync(cancellationToken);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_owns || _disposed)
            return;

        _disposed = true;
        if (!_committed)
        {
            try
            {
                await _transaction.RollbackAsync();
            }
            catch (InvalidOperationException)
            {
                // Commit or an earlier rollback already completed the transaction.
            }
        }

        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
