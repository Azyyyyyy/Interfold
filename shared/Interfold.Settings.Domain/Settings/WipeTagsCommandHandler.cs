using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Settings.Domain.Settings;

public sealed class WipeTagsCommandHandler : IdempotentCommandHandler<WipeTagsCommand, SettingsCommandResult>
{
    private readonly IClusterEventBus _eventBus;
    private readonly ITagRepository _tagRepository;
    private readonly IStorageTransactionFactory _transactions;

    public WipeTagsCommandHandler(
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        ITagRepository tagRepository,
        IStorageTransactionFactory transactions)
        : base(idempotencyStore)
    {
        _eventBus = eventBus;
        _tagRepository = tagRepository;
        _transactions = transactions;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsTagsWipe;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<WipeTagsCommand> command,
        CancellationToken cancellationToken)
        => SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            async ct =>
            {
                var systemId = command.PrincipalId;
                var tags = await _tagRepository.ListAsync(systemId, ct);

                await using var transaction = await _transactions.BeginAsync(ct);
                foreach (var tag in tags)
                    await _tagRepository.DeleteAsync(systemId, tag.Id, ct);

                await transaction.CommitAsync(ct);
                return true;
            },
            SettingsAction.TagsWiped.ToFailedEntityRef(),
            SettingsAction.TagsWiped,
            ct => _eventBus.PublishAsync(new SettingsTagsWipedSignalEvent(command.PrincipalId), ct),
            cancellationToken);
}
