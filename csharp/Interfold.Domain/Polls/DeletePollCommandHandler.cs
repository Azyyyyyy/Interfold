using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Polls;

public sealed class DeletePollCommandHandler : IdempotentCommandHandler<DeletePollCommand, PollCommandResult>
{
    private readonly IPollRepository _pollRepository;
    private readonly IClusterEventBus _eventBus;

    public DeletePollCommandHandler(
        IPollRepository pollRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _pollRepository = pollRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.PollDelete;

    protected override PollCommandResult CreateReplayResult(PollCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<PollCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DeletePollCommand> command,
        CancellationToken cancellationToken = default)
    {

        var exists = await _pollRepository.ExistsAsync(command.PrincipalId, command.Payload.PollId, cancellationToken);
        if (!exists)
            return RejectInvariant(command, EntityRefs.PollNotFound);

        var deleted = await _pollRepository.DeleteAsync(command.PrincipalId, command.Payload.PollId, cancellationToken);
        if (!deleted)
            return RejectInvariant(command, EntityRefs.PollDeleteFailed);

        var result = new PollCommandResult(command.PrincipalId, command.Payload.PollId, Replay: false);

        await _eventBus.PublishAsync(new PollDeletedEvent(command.PrincipalId, command.Payload.PollId), cancellationToken);
        return CommandExecutionResult<PollCommandResult>.Success(result);
    }

}
