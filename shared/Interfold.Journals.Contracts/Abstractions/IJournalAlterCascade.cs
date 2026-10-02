using Interfold.Journals.Contracts.Ids;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Abstractions;

/// <summary>Alter-journal entries removed and global journals the alter was detached from.
/// The global journal rows themselves stay.</summary>
public sealed record JournalAlterCascadeResult(
    IReadOnlyList<EntryId> DeletedEntryIds,
    IReadOnlyList<EntryId> DetachedGlobalJournalIds)
{
    public static JournalAlterCascadeResult None { get; } = new([], []);
}

/// <summary>Cascade hook for alter deletion. Adapter over <c>IJournalRepository</c>, registered by <c>AddJournalsModule</c>.</summary>
public interface IJournalAlterCascade
{
    Task<JournalAlterCascadeResult> DeleteAllForAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);
}
