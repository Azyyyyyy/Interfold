using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Fronting;

public sealed class SetFrontCommandHandler : IdempotentCommandHandler<SetFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;
    private readonly TimeProvider _timeProvider;

    public SetFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        TimeProvider timeProvider) : base(idempotencyStore)
    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
        _timeProvider = timeProvider;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingSet;

    protected override FrontCommandResult CreateReplayResult(FrontCommandResult originalResult) =>
        originalResult with { Replay = true };

    protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync(
            CommandEnvelope<SetFrontCommand> command,
            CancellationToken cancellationToken = default)
    {
        if (command.Payload.AlterId.Value is < 1 or > 32_767)
            return RejectInvariant(command, EntityRefs.FrontingInvalidAlterId);

        if ((command.Payload.Comment?.Length ?? 0) > 50)
            return RejectInvariant(command, EntityRefs.FrontingInvalidComment);

        // "Set" semantics: after this completes, the target alter is the sole fronter.
        // The previous implementation rejected with `fronting:already_fronting` whenever the
        // target was already in the active set - which broke the obvious use case of "promote
        // X to the only fronter when X is one of several". It also ended every other fronter
        // silently (only FrontingSetEvent was published, no per-alter FrontingEndedEvent),
        // so clients never received the per-alter "stopped fronting" signals they need to
        // update local state.
        //
        // New rules:
        //   - End every active alter that isn't the target. Publish FrontingEndedEvent per
        //     ended alter so socket clients see them stop one-by-one (matches the contract
        //     EndFrontCommandHandler already follows for a single end).
        //   - If the target was already fronting, keep its front row (preserves the front_id,
        //     start_time and history) - reuse its existing front id in the FrontingSetEvent.
        //     If it wasn't, start it.
        //   - If anything that was the primary got ended (or the previous primary was the
        //     now-set target and we cleared primary), publish FrontingPrimaryChangedEvent(null).
        //     "set" always clears primary because there's exactly one fronter afterwards.
        //   - Always publish FrontingSetEvent for the target so clients pin the now-only-front.
        var active = await _frontingRepository.ListActiveAsync(command.PrincipalId, cancellationToken);
        var targetActive = active.FirstOrDefault(f => f.Alter.Id == command.Payload.AlterId);
        var others = active.Where(f => f.Alter.Id != command.Payload.AlterId).ToArray();
        var primaryWasPresent = active.Any(f => f.Primary);

        var endedAt = _timeProvider.GetUtcNow();
        foreach (var other in others)
        {
            // Best-effort: a single end failure shouldn't poison the whole set. The repo's
            // EndAsync returns false when there's no live row for the alter (race: another
            // request ended it between ListActive and EndAsync) - we silently move on.
            await _frontingRepository.EndAsync(command.PrincipalId, other.Front.AlterId, endedAt, cancellationToken);
        }

        FrontId frontId;
        if (targetActive is not null)
        {
            // Target was already fronting - preserve its front row (front_id, start_time).
            frontId = targetActive.Front.Id;
        }
        else
        {
            var started = await _frontingRepository.StartAsync(
                command.PrincipalId,
                command.Payload.AlterId,
                command.Payload.Comment,
                _timeProvider.GetUtcNow(),
                cancellationToken);

            if (started is null)
                return RejectInvariant(command, EntityRefs.FrontingStartFailed);

            frontId = started.Value;
        }

        // "set" semantics: after the call there's a single fronter, so any primary designation
        // is moot. Clear unconditionally (no-op when there wasn't one).
        await _frontingRepository.SetPrimaryAsync(command.PrincipalId, null, cancellationToken);

        var result = new FrontCommandResult(command.PrincipalId, command.Payload.AlterId, frontId, Replay: false);

        await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);

        // Per-alter FrontingEndedEvent for every alter that was ended by this set. Clients
        // listening on the socket layer rely on this to clear those alters from their
        // local "currently fronting" view.
        foreach (var other in others)
        {
            await _eventBus.PublishAsync(
                new FrontingEndedEvent(command.PrincipalId, other.Front.AlterId),
                cancellationToken);
        }

        // FrontingPrimaryChangedEvent only fires when primary actually transitioned away from
        // a real value - emitting it unconditionally would spam clients with no-op events
        // every time `set` is called against a no-primary state.
        if (primaryWasPresent)
        {
            await _eventBus.PublishAsync(
                new FrontingPrimaryChangedEvent(command.PrincipalId, null),
                cancellationToken);
        }

        // Emit granular event for socket layer to handle fronting_set. Always fires - even
        // when the target was already the only fronter (idempotent "set" returns success
        // with the existing front id; the client may have called this to recover from a
        // desync and should still receive the event).
        await _eventBus.PublishAsync(new FrontingSetEvent(command.PrincipalId, frontId), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<SetFrontCommand> command,
        EntityRef entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}