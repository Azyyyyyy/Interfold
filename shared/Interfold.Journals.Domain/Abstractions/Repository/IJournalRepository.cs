using Interfold.Journals.Contracts.Ids;
using Interfold.Journals.Contracts.Models.Commands;
using Interfold.Journals.Contracts.Models.Read;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Journals.Domain.Abstractions.Repository;

public interface IJournalRepository
{
    Task<EntryId?> CreateGlobalAsync(SystemId systemId, CreateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default);

    Task<bool> ExistsGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default);

    Task<bool> UpdateGlobalAsync(SystemId systemId, UpdateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default);

    Task<bool> SetGlobalLockedAsync(SystemId systemId, EntryId entryId, bool locked, CancellationToken cancellationToken = default);

    Task<bool> SetGlobalPinnedAsync(SystemId systemId, EntryId entryId, bool pinned, CancellationToken cancellationToken = default);

    Task<bool> AttachGlobalAlterAsync(SystemId systemId, EntryId entryId, AlterId alterId, CancellationToken cancellationToken = default);

    Task<bool> DetachGlobalAlterAsync(SystemId systemId, EntryId entryId, AlterId alterId, CancellationToken cancellationToken = default);

    Task<EntryId?> CreateAlterAsync(SystemId systemId, CreateAlterJournalEntryCommand command, CancellationToken cancellationToken = default);

    Task<AlterJournalRef?> GetAlterRefAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default);

    Task<bool> UpdateAlterAsync(SystemId systemId, UpdateAlterJournalEntryCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteAlterAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the alter's journal entries and detaches it from global journals. Global journal rows stay.</summary>
    Task<JournalAlterCascadeResult> DeleteAllForAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);

    Task<bool> SetAlterLockedAsync(SystemId systemId, EntryId entryId, bool locked, CancellationToken cancellationToken = default);

    Task<bool> SetAlterPinnedAsync(SystemId systemId, EntryId entryId, bool pinned, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlterJournalReadModel>> ListAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);

    Task<AlterJournalReadModel?> GetAlterAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JournalReadModel>> ListGlobalAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<JournalReadModel?> GetGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default);
}