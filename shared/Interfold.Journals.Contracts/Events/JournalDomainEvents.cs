using Interfold.Journals.Contracts.Ids;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Journals.Contracts.Events;

public sealed record GlobalJournalEntryCreatedEvent(SystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryUpdatedEvent(SystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryDeletedEvent(SystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryCreatedEvent(SystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryUpdatedEvent(SystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryDeletedEvent(SystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;
