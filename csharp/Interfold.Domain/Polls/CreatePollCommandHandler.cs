using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Polls;

public sealed class CreatePollCommandHandler : IdempotentCommandHandler<CreatePollCommand, PollCommandResult>
{
    private readonly IPollRepository _pollRepository;
    private readonly IClusterEventBus _eventBus;

    public CreatePollCommandHandler(
        IPollRepository pollRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _pollRepository = pollRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.PollCreate;

protected override async Task<CommandExecutionResult<PollCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<CreatePollCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (RejectIfBlank(command, command.Payload.Title, EntityRefs.PollTitleRequired) is { } blankReject)
            return blankReject;

        if (command.Payload.Title.Length > 100)
            return RejectInvariant(command, EntityRefs.PollTitleTooLong);

        if (!string.IsNullOrWhiteSpace(command.Payload.Description) && command.Payload.Description.Length > 2000)
            return RejectInvariant(command, EntityRefs.PollDescriptionTooLong);

        // Stamp the row with the envelope's OccurredAt rather than honouring whatever
        // InsertedAtUtc the caller put on the payload. The caller (PollsController) sends
        // `default(DateTime)` precisely so the idempotency hash stays stable across retries
        // — see PollsController.Create. The SP import bypasses this handler entirely and
        // sets InsertedAtUtc itself from lastOperationTime, so it isn't affected here.
        // OccurredAt is nullable on the envelope; fall back to UtcNow if missing.
        var insertedAtUtc = (command.OccurredAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        var enrichedPayload = command.Payload with { InsertedAtUtc = insertedAtUtc };
        var pollId = await _pollRepository.CreateAsync(command.PrincipalId, enrichedPayload, cancellationToken);
        if (pollId is null)
            return RejectInvariant(command, EntityRefs.PollCreateFailed);

        var result = new PollCommandResult(command.PrincipalId, pollId.Value, Replay: false);

        await _eventBus.PublishAsync(new PollCreatedEvent(command.PrincipalId, pollId.Value), cancellationToken);
        return CommandExecutionResult<PollCommandResult>.Success(result);
    }

}
