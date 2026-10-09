using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Settings.Contracts.Events;

public sealed record SettingsFieldsChangedEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsProfileUpdatedEvent(SystemId TargetSystemId, bool EmitUsernameUpdated) : ITargetedClusterEvent;

public sealed record SettingsAccountDeletedSignalEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsAltersWipedSignalEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsEncryptedDataWipedSignalEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsDiscordAccountUnlinkedSignalEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsAppleAccountUnlinkedSignalEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

// One event per provider (mirrors the per-provider unlink signal events below) so the
// identity carries its real type and neither producer nor consumer switches on a provider enum.
public sealed record SettingsDiscordAccountLinkedEvent(SystemId TargetSystemId, DiscordId DiscordId) : ITargetedClusterEvent;

public sealed record SettingsGoogleAccountLinkedEvent(SystemId TargetSystemId, Email Email) : ITargetedClusterEvent;

public sealed record SettingsAppleAccountLinkedEvent(SystemId TargetSystemId, AppleId AppleId) : ITargetedClusterEvent;

public sealed record SettingsGoogleAccountUnlinkedSignalEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsTagsWipedSignalEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SimplyPluralImportCompletedEvent(SystemId TargetSystemId, int AlterCount) : ITargetedClusterEvent;

public sealed record SimplyPluralImportFailedEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record PluralKitImportCompletedEvent(SystemId TargetSystemId, int AlterCount) : ITargetedClusterEvent;

public sealed record PluralKitImportFailedEvent(SystemId TargetSystemId) : ITargetedClusterEvent;
