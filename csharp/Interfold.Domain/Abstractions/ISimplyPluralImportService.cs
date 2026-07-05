using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions;

/// <summary>
/// Performs a full data import from Simply Plural for a given system.
/// </summary>
public interface ISimplyPluralImportService
{
    Task<SpImportResult> ImportAsync(
        SystemId systemId,
        ImportToken spToken,
        RecoveryCode? encryptionKey,
        CancellationToken cancellationToken = default);

    bool? WaitForAvatars { get; set; }
}

public sealed record SpImportResult(bool Success, int AlterCount, string? Error = null);
