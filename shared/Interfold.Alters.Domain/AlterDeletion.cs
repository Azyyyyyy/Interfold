using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Polls.Contracts.Ids;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Tags.Contracts.Ids;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Alters.Domain;

public sealed record AlterDeletionResult(
    FrontAlterRemoval Fronts,
    IReadOnlyList<TagId> DetachedTagIds,
    JournalAlterCascadeResult Journals,
    IReadOnlyList<PollId> UpdatedPollIds);

public interface IAlterDeletion
{
    /// <summary>Null when the alter row was not removed. Related deletes roll back with the scope.</summary>
    Task<AlterDeletionResult?> DeleteAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);
}

public sealed class AlterDeletion : IAlterDeletion
{
    private readonly IStorageTransactionFactory _transactions;
    private readonly IFrontingRepository _fronting;
    private readonly ITagRepository _tags;
    private readonly IJournalAlterCascade _journals;
    private readonly IPollRepository _polls;
    private readonly IAlterRepository _alters;

    public AlterDeletion(
        IStorageTransactionFactory transactions,
        IFrontingRepository fronting,
        ITagRepository tags,
        IJournalAlterCascade journals,
        IPollRepository polls,
        IAlterRepository alters)
    {
        _transactions = transactions;
        _fronting = fronting;
        _tags = tags;
        _journals = journals;
        _polls = polls;
        _alters = alters;
    }

    public async Task<AlterDeletionResult?> DeleteAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _transactions.BeginAsync(cancellationToken);

        var fronts = await _fronting.DeleteAllForAlterAsync(systemId, alterId, cancellationToken);
        var tags = await _tags.DetachAllForAlterAsync(systemId, alterId, cancellationToken);
        var journals = await _journals.DeleteAllForAlterAsync(systemId, alterId, cancellationToken);
        var polls = await _polls.RemoveAlterFromPollsAsync(systemId, alterId, cancellationToken);
        if (!await _alters.DeleteAsync(systemId, alterId, cancellationToken))
            return null;

        await transaction.CommitAsync(cancellationToken);
        return new AlterDeletionResult(fronts, tags, journals, polls);
    }
}
