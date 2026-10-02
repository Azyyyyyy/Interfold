using System.Runtime.CompilerServices;

namespace Interfold.Shared.Domain.Abstractions;

/// <summary>
/// Storage scope a command handler opens around several repository calls.
/// <see cref="DisposeAsync"/> rolls the work back unless <see cref="CommitAsync"/> completed.
/// </summary>
public interface IStorageTransaction : IAsyncDisposable
{
    void Join();

    Task CommitAsync(CancellationToken cancellationToken = default);
}

public interface IStorageTransactionFactory
{
    StorageTransactionStart BeginAsync(CancellationToken cancellationToken = default);
}

public readonly struct StorageTransactionStart
{
    private readonly ValueTask<IStorageTransaction> _open;

    public StorageTransactionStart(ValueTask<IStorageTransaction> open) => _open = open;

    public StorageTransactionAwaiter GetAwaiter() => new(_open);
}

public readonly struct StorageTransactionAwaiter : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<IStorageTransaction> _awaiter;

    public StorageTransactionAwaiter(ValueTask<IStorageTransaction> open) => _awaiter = open.GetAwaiter();

    public bool IsCompleted => _awaiter.IsCompleted;

    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);

    public IStorageTransaction GetResult()
    {
        var transaction = _awaiter.GetResult();
        // AsyncLocal written inside BeginAsync does not flow back to the awaiting handler.
        transaction.Join();
        return transaction;
    }
}

/// <summary>
/// Scylla and the in-memory store apply each repository call as it runs, so disposing
/// this scope cannot undo those writes. SQLite is the implementation that actually rolls back.
/// </summary>
public sealed class ImmediateStorageTransactionFactory : IStorageTransactionFactory
{
    public StorageTransactionStart BeginAsync(CancellationToken cancellationToken = default)
        => new(ValueTask.FromResult<IStorageTransaction>(new ImmediateStorageTransaction()));

    private sealed class ImmediateStorageTransaction : IStorageTransaction
    {
        public void Join()
        {
        }

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
