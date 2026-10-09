using Interfold.Auth.Contracts.Models.Commands;
using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Auth.Domain;

public sealed record LinkOAuthIdentityCommandResult(AccountLinkResult Result, SystemId? SystemId);

public sealed class LinkOAuthIdentityCommandHandler : ICommandHandler<LinkOAuthIdentityCommand, LinkOAuthIdentityCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IClusterEventBus _eventBus;

    public LinkOAuthIdentityCommandHandler(
        IAccountRepository accountRepository,
        IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<LinkOAuthIdentityCommandResult>> HandleAsync(CommandEnvelope<LinkOAuthIdentityCommand> command, CancellationToken cancellationToken = default)
    {
        var linkToken = command.Payload.LinkToken;
        var identity = command.Payload.Identity;

        var systemId = await _accountRepository.ResolveSystemIdByLinkTokenAsync(linkToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(systemId?.Value))
            return CommandHandler.RejectInvariant<LinkOAuthIdentityCommandResult>(command.OperationId, EntityRefs.AuthLinkInvalidToken);

        await _accountRepository.ClearLinkTokenAsync(systemId.Value, cancellationToken);

        var result = await _accountRepository.LinkIdentityToUserAsync(systemId.Value, identity, cancellationToken);

        if (result == AccountLinkResult.Success)
        {
            switch (identity)
            {
                case { Discord: { } discordId }:
                    await _eventBus.PublishAsync(
                        new SettingsDiscordAccountLinkedEvent(systemId.Value, discordId),
                        cancellationToken);
                    break;
                case { Google: { } email }:
                    await _eventBus.PublishAsync(
                        new SettingsGoogleAccountLinkedEvent(systemId.Value, email),
                        cancellationToken);
                    break;
                case { Apple: { } appleId }:
                    await _eventBus.PublishAsync(
                        new SettingsAppleAccountLinkedEvent(systemId.Value, appleId),
                        cancellationToken);
                    break;
            }
        }

        return CommandExecutionResult<LinkOAuthIdentityCommandResult>.Success(new LinkOAuthIdentityCommandResult(result, systemId));
    }
}
