using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Alters;

public sealed class CreateAlterCommandHandler : IdempotentCommandHandler<CreateAlterCommand, AlterCommandResult>
{
    private readonly IAlterRepository _alterRepository;
    private readonly IClusterEventBus _eventBus;

    public CreateAlterCommandHandler(
        IAlterRepository alterRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
:base(idempotencyStore)    {
        _alterRepository = alterRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.AlterCreate;

    protected override AlterCommandResult CreateReplayResult(AlterCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<AlterCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<CreateAlterCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Name))
        {
            return RejectInvariant(command, EntityRefs.AlterName);
        }
        
        var alterId = await _alterRepository.CreateAsync(command.PrincipalId, command.Payload, cancellationToken);
        if (alterId is null)
        {
            return RejectInvariant(command, EntityRefs.AlterCreate);
        }

        var result = new AlterCommandResult(command.PrincipalId, alterId.Value, Replay: false);

        await _eventBus.PublishAsync(
            new AlterCreatedEvent(command.PrincipalId, alterId.Value),
            cancellationToken);

        return CommandExecutionResult<AlterCommandResult>.Success(result);
    }

}