using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Tags;

public sealed class RemoveParentTagCommandHandler : IdempotentCommandHandler<RemoveParentTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IClusterEventBus _eventBus;

    public RemoveParentTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _tagRepository = tagRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.TagRemoveParent;

    protected override TagCommandResult CreateReplayResult(TagCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<TagCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<RemoveParentTagCommand> command,
        CancellationToken cancellationToken = default)
    {
        var tagId = command.Payload.TagId;

        var found = await _tagRepository.RemoveParentAsync(command.PrincipalId, tagId, cancellationToken);
        if (!found) return RejectInvariant(command, EntityRefs.TagNotFound);

        var result = new TagCommandResult(command.PrincipalId, tagId, Replay: false);

        await _eventBus.PublishAsync(
            new TagUpdatedEvent(command.PrincipalId, tagId),
            cancellationToken);

        return CommandExecutionResult<TagCommandResult>.Success(result);
    }

    private static CommandExecutionResult<TagCommandResult> RejectInvariant(
        CommandEnvelope<RemoveParentTagCommand> command, EntityRef entityRef) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}
