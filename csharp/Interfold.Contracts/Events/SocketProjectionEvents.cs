using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Events;

public sealed record AlterCreatedEvent(SystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

public sealed record AlterUpdatedEvent(SystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

public sealed record AlterDeletedEvent(SystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

public sealed record TagCreatedEvent(SystemId TargetSystemId, TagId TagId) : ITargetedClusterEvent;

public sealed record TagUpdatedEvent(SystemId TargetSystemId, TagId TagId) : ITargetedClusterEvent;

public sealed record TagDeletedEvent(SystemId TargetSystemId, TagId TagId) : ITargetedClusterEvent;

public sealed record SettingsFieldsChangedEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsProfileUpdatedEvent(SystemId TargetSystemId, bool EmitUsernameUpdated) : ITargetedClusterEvent;

public sealed record SettingsAccountDeletedSignalEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsAltersWipedSignalEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsEncryptedDataWipedSignalEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsDiscordAccountUnlinkedSignalEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsAppleAccountUnlinkedSignalEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record PollCreatedEvent(SystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;

public sealed record PollUpdatedEvent(SystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;

public sealed record PollDeletedEvent(SystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryCreatedEvent(SystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryUpdatedEvent(SystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryDeletedEvent(SystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryCreatedEvent(SystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryUpdatedEvent(SystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryDeletedEvent(SystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record FriendshipAddedEvent(SystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendshipRemovedEvent(SystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendshipTrustedEvent(SystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendshipUntrustedEvent(SystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendRequestSentEvent(SystemId TargetSystemId, SystemId ToSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestReceivedEvent(SystemId TargetSystemId, SystemId FromSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestRemovedFromEvent(SystemId TargetSystemId, SystemId FromSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestRemovedToEvent(SystemId TargetSystemId, SystemId ToSystemId) : ITargetedClusterEvent;

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
