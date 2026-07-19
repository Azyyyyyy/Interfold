using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Tags;

public sealed class DetachAlterFromTagCommandHandler : IdempotentCommandHandler<DetachAlterFromTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IAlterRepository _alterRepository;
    private readonly IClusterEventBus _eventBus;

    public DetachAlterFromTagCommandHandler(
        ITagRepository tagRepository,
        IAlterRepository alterRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _tagRepository = tagRepository;
        _alterRepository = alterRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.TagDetachAlter;

    protected override TagCommandResult CreateReplayResult(TagCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<TagCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DetachAlterFromTagCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payload = command.Payload;

        var alterExists = await _alterRepository.ExistsAsync(command.PrincipalId, payload.AlterId, cancellationToken);
        if (!alterExists) return RejectInvariant(command, EntityRefs.TagAlterNotFound);

        var detached = await _tagRepository.DetachAlterAsync(
            command.PrincipalId, payload.TagId, payload.AlterId, cancellationToken);

        if (!detached) return RejectInvariant(command, EntityRefs.TagNotFound);

        var result = new TagCommandResult(command.PrincipalId, payload.TagId, Replay: false);

        await _eventBus.PublishAsync(
            new TagUpdatedEvent(command.PrincipalId, payload.TagId),
            cancellationToken);

        return CommandExecutionResult<TagCommandResult>.Success(result);
    }

}
