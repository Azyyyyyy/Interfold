using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Tags;

public sealed class CreateTagCommandHandler : IdempotentCommandHandler<CreateTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IClusterEventBus _eventBus;

    public CreateTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
:base(idempotencyStore)    {
        _tagRepository = tagRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.TagCreate;

    protected override TagCommandResult CreateReplayResult(TagCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<TagCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<CreateTagCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Name))
            return RejectInvariant(command, EntityRefs.TagNameRequired);

        if (command.Payload.Name.Length > 50)
            return RejectInvariant(command, EntityRefs.TagNameTooLong);

        if (command.Payload.ParentTagId is { } parentTagId && parentTagId != TagId.Empty)
        {
            var parentExists = await _tagRepository.ExistsAsync(
                command.PrincipalId,
                parentTagId,
                cancellationToken
            );

            if (!parentExists)
                return RejectInvariant(command, EntityRefs.TagParentNotFound);
        }

        // See CreateTagCommand XML-doc: public-API callers send `default(DateTime)` so the
        // idempotency hash stays stable across retries. We stamp the real value here from
        // the envelope just before the repo call. The SP import bypasses this handler and
        // sets InsertedAtUtc itself from the decoded ObjectId, so it isn't affected here.
        var insertedAtUtc = (command.OccurredAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        var tagId = await _tagRepository.CreateAsync(
            command.PrincipalId,
            command.Payload with { InsertedAtUtc = insertedAtUtc },
            cancellationToken);
        if (tagId is null)
            return RejectInvariant(command, EntityRefs.TagCreateFailed);

        var result = new TagCommandResult(command.PrincipalId, tagId.Value, Replay: false);

        await _eventBus.PublishAsync(
            new TagCreatedEvent(command.PrincipalId, tagId.Value),
            cancellationToken);

        return CommandExecutionResult<TagCommandResult>.Success(result);
    }

}
