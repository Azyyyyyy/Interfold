using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Tags;

public sealed class UpdateTagCommandHandler : IdempotentCommandHandler<UpdateTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdateTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _tagRepository = tagRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.TagUpdate;

    protected override TagCommandResult CreateReplayResult(TagCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<TagCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<UpdateTagCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payload = command.Payload;

        if (payload.Name is null && payload.Color is null && payload.Description is null && payload.SecurityLevel is null)
            return RejectInvariant(command, EntityRefs.TagNoFields);

        if (payload.Name is not null && payload.Name.Length > 50)
            return RejectInvariant(command, EntityRefs.TagNameTooLong);

        var found = await _tagRepository.UpdateAsync(command.PrincipalId, payload, cancellationToken);
        if (!found) return RejectInvariant(command, EntityRefs.TagNotFound);

        var result = new TagCommandResult(command.PrincipalId, payload.TagId, Replay: false);

        await _eventBus.PublishAsync(
            new TagUpdatedEvent(command.PrincipalId, payload.TagId),
            cancellationToken);

        return CommandExecutionResult<TagCommandResult>.Success(result);
    }

    private static CommandExecutionResult<TagCommandResult> RejectInvariant(
        CommandEnvelope<UpdateTagCommand> command, EntityRef entityRef) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}
