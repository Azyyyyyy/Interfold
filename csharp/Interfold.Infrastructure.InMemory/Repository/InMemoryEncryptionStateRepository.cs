using System.Collections.Concurrent;
using Interfold.Contracts.Models;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryEncryptionStateRepository : IEncryptionStateRepository
{
    private readonly ConcurrentDictionary<string, EncryptionState> _states = new(StringComparer.Ordinal);

    public Task<EncryptionState?> GetAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = NormalizeSystemId(systemId);
        _states.TryGetValue(normalizedSystemId, out var state);
        return Task.FromResult(state);
    }

    public Task<bool> UpsertAsync(SystemId systemId, bool initialized, KeyChecksum? keyChecksum, EncryptionSalt? salt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = NormalizeSystemId(systemId);

        _states.TryGetValue(normalizedSystemId, out EncryptionState? value);
        _states[normalizedSystemId] = new EncryptionState(Initialized: initialized, KeyChecksum: keyChecksum,
            Salt: salt ?? value?.Salt);

        return Task.FromResult(true);
    }

    private static string NormalizeSystemId(SystemId systemId) => InMemoryStorageKeys.NormalizeSystemId(systemId);

    private static string NormalizeSystemId(string systemId) => InMemoryStorageKeys.NormalizeSystemId(systemId);
}
