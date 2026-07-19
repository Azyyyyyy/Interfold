using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Accounts;

public sealed class UpdateUsernameCommandHandler : IdempotentCommandHandler<UpdateUsernameCommand, AccountCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdateUsernameCommandHandler(
        IAccountRepository accountRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
:base(idempotencyStore)    {
        _accountRepository = accountRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.AccountUsernameUpdate;

    protected override AccountCommandResult CreateReplayResult(AccountCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<AccountCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<UpdateUsernameCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Username))
        {
            return RejectInvariant(command, EntityRefs.AccountUsernameInvalid);
        }

        if (command.Payload.Username.Value.Length > 64)
        {
            return RejectInvariant(command, EntityRefs.AccountUsernameTooLong);
        }

        var persisted = await _accountRepository.UpdateUsernameAsync(
            command.PrincipalId,
            command.Payload.Username,
            cancellationToken
        );

        if (!persisted)
        {
            return RejectInvariant(command, EntityRefs.AccountUsernameUpdateFailed);
        }

        var result = new AccountCommandResult(command.PrincipalId, command.Payload.Username, Replay: false);

        await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, true), cancellationToken);
        return CommandExecutionResult<AccountCommandResult>.Success(result);
    }

}
