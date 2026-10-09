using Interfold.Alters.Domain;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Friendships.Contracts.Events;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Settings.Domain.Settings;

public sealed class DeleteAccountCommandHandler : IdempotentCommandHandler<DeleteAccountCommand, SettingsCommandResult>
{
    private readonly IClusterEventBus _eventBus;
    private readonly IAccountRepository _accountRepository;
    private readonly IAlterRepository _alterRepository;
    private readonly IAlterDeletion _deletion;
    private readonly ITagRepository _tagRepository;
    private readonly IPollRepository _pollRepository;
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IJournalRepository _journalRepository;
    private readonly IFriendshipRepository _friendshipRepository;
    private readonly IStorageTransactionFactory _transactions;

    public DeleteAccountCommandHandler(
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        IAccountRepository accountRepository,
        IAlterRepository alterRepository,
        IAlterDeletion deletion,
        ITagRepository tagRepository,
        IPollRepository pollRepository,
        ISettingsFieldRepository fieldRepository,
        IJournalRepository journalRepository,
        IFriendshipRepository friendshipRepository,
        IStorageTransactionFactory transactions)
        : base(idempotencyStore)
    {
        _eventBus = eventBus;
        _accountRepository = accountRepository;
        _alterRepository = alterRepository;
        _deletion = deletion;
        _tagRepository = tagRepository;
        _pollRepository = pollRepository;
        _fieldRepository = fieldRepository;
        _journalRepository = journalRepository;
        _friendshipRepository = friendshipRepository;
        _transactions = transactions;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsAccountDelete;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<DeleteAccountCommand> command,
        CancellationToken cancellationToken)
    {
        // Shared between the mutation and publish lambdas — the friendship repo returns the
        // set of counterparties whose Friendship rows we tore down, and each one needs a
        // FriendshipRemovedEvent on the cluster bus so their sockets refresh their
        // friend-list. IdempotentCommandHandler skips ExecuteCoreAsync on replay, so this
        // list is only ever populated once per idempotency key — same guard the previous
        // ExecuteAndPublishAsync gave us via `Result.Replay: false`!.
        var unfriendedIds = new List<SystemId>();

        return SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            async ct =>
            {
                var systemId = command.PrincipalId;
                var alters = await _alterRepository.ListAsync(systemId, ct);
                var tags = await _tagRepository.ListAsync(systemId, ct);
                var polls = await _pollRepository.ListAsync(systemId, ct);
                var fields = await _fieldRepository.ListAsync(systemId, ct);
                var globalEntries = await _journalRepository.ListGlobalAsync(systemId, ct);

                await using var transaction = await _transactions.BeginAsync(ct);

                foreach (var alter in alters)
                {
                    if (await _deletion.DeleteAsync(systemId, alter.Id, ct) is null)
                        return false;

                    //TODO: Delete alter image if it exists
                }

                foreach (var tag in tags)
                    await _tagRepository.DeleteAsync(systemId, tag.Id, ct);

                foreach (var poll in polls)
                    await _pollRepository.DeleteAsync(systemId, poll.Id, ct);

                foreach (var field in fields)
                    await _fieldRepository.DeleteAsync(systemId, field.Id, ct);

                foreach (var entry in globalEntries)
                    await _journalRepository.DeleteGlobalAsync(systemId, entry.Id, ct);

                var deletedIds = await _friendshipRepository.DeleteAllForSystemAsync(systemId, ct);
                unfriendedIds.AddRange(deletedIds);

                if (!await _accountRepository.DeleteAsync(systemId, ct))
                    return false;

                //TODO: Delete account image if it exists
                await transaction.CommitAsync(ct);
                return true;
            },
            SettingsAction.AccountDeleted.ToFailedEntityRef(),
            SettingsAction.AccountDeleted,
            async ct =>
            {
                await _eventBus.PublishAsync(new SettingsAccountDeletedSignalEvent(command.PrincipalId), ct);

                foreach (var friendId in unfriendedIds)
                {
                    await _eventBus.PublishAsync(new FriendshipRemovedEvent(friendId, command.PrincipalId), ct);
                }
            },
            cancellationToken);
    }
}
