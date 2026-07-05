using Interfold.Contracts.Models;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions.Repository;

public interface IEncryptionStateRepository
{
    Task<EncryptionState?> GetAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<bool> UpsertAsync(SystemId systemId, bool initialized, string? keyChecksum, string? salt, CancellationToken cancellationToken = default);
}
