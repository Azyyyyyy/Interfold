using Interfold.Alters.Contracts.Events;
using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Alters.Domain;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Fronting.Contracts.Events;
using Interfold.Fronting.Contracts.Ids;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Journals.Contracts.Events;
using Interfold.Journals.Contracts.Ids;
using Interfold.Polls.Contracts.Events;
using Interfold.Polls.Contracts.Ids;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Tags.Contracts.Events;
using Interfold.Tags.Contracts.Ids;

namespace Interfold.Api.UnitTests.Domain;

public sealed class DeleteAlterCascadeEventTests
{
    private static readonly ScopedSystemId Principal =
        ScopedSystemId.Compose(ScyllaKeyspace.Nam, "altdel");

    [Test]
    public async Task DeleteAlter_PublishesEventsForCascadedRows()
    {
        var alterId = new AlterId(3);
        var frontId = new FrontId(Guid.NewGuid());
        var tagId = new TagId(Guid.NewGuid());
        var journalId = new EntryId(Guid.NewGuid());
        var globalId = new EntryId(Guid.NewGuid());
        var pollId = new PollId(Guid.NewGuid());

        var repo = new StubAlterRepository();
        var deletion = new StubDeletion
        {
            Result = new AlterDeletionResult(
                new FrontAlterRemoval([frontId], HadActiveFront: true, PrimaryCleared: true),
                [tagId],
                new JournalAlterCascadeResult([journalId], [globalId]),
                [pollId]),
        };
        var bus = new RecordingEventBus();
        var handler = new DeleteAlterCommandHandler(repo, deletion, new NoopIdempotencyStore(), bus);

        var result = await handler.HandleAsync(Envelope(alterId));

        await Assert.That(result.Accepted).IsTrue();
        await Assert.That(bus.Events).IsEquivalentTo(new object[]
        {
            new FrontingStateChangedEvent(Principal),
            new FrontingEndedEvent(Principal, alterId),
            new FrontDeletedEvent(Principal, frontId),
            new FrontingPrimaryChangedEvent(Principal, null),
            new TagUpdatedEvent(Principal, tagId),
            new AlterJournalEntryDeletedEvent(Principal, journalId),
            new GlobalJournalEntryUpdatedEvent(Principal, globalId),
            new PollUpdatedEvent(Principal, pollId),
            new AlterDeletedEvent(Principal, alterId),
        });
        await Assert.That(bus.Events[0]).IsTypeOf<FrontingStateChangedEvent>();
        await Assert.That(bus.Events[^1]).IsTypeOf<AlterDeletedEvent>();
    }

    [Test]
    public async Task DeleteAlter_MissingAlter_DoesNotPublish()
    {
        var repo = new StubAlterRepository { Exists = false };
        var deletion = new StubDeletion();
        var bus = new RecordingEventBus();
        var handler = new DeleteAlterCommandHandler(repo, deletion, new NoopIdempotencyStore(), bus);

        var result = await handler.HandleAsync(Envelope(new AlterId(4)));

        await Assert.That(result.Accepted).IsFalse();
        await Assert.That(bus.Events).IsEmpty();
        await Assert.That(deletion.Calls).IsEqualTo(0);
    }

    private static CommandEnvelope<DeleteAlterCommand> Envelope(AlterId alterId) =>
        new(
            OperationId: OperationIds.AlterDelete,
            CommandId: Guid.NewGuid(),
            PrincipalId: Principal,
            IdempotencyKey: new IdempotencyKey(Guid.NewGuid().ToString("N")),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAlterCommand(alterId));

    private sealed class StubAlterRepository : IAlterRepository
    {
        public bool Exists { get; init; } = true;

        public Task<bool> ExistsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
            => Task.FromResult(Exists);

        public Task<bool> DeleteAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveFieldValuesAsync(SystemId systemId, FieldId fieldId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AlterId?> CreateAsync(SystemId systemId, Interfold.Alters.Contracts.Models.Commands.CreateAlterCommand command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> UpdateAsync(SystemId systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Interfold.Alters.Contracts.Models.AlterReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Interfold.Alters.Contracts.Models.BareAlter>> ListGuardedAsync(SystemId systemId, SystemId? viewerSystemId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Interfold.Alters.Contracts.Models.AlterReadModel?> GetAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Interfold.Alters.Contracts.Models.BareAlter?> GetGuardedAsync(SystemId systemId, AlterId alterId, SystemId? viewerSystemId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> AliasTakenByOtherAsync(SystemId systemId, AlterId alterId, string alias, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubDeletion : IAlterDeletion
    {
        public int Calls;
        public AlterDeletionResult? Result { get; init; }

        public Task<AlterDeletionResult?> DeleteAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingEventBus : IClusterEventBus
    {
        public List<object> Events { get; } = [];

        public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : class
        {
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(ScopedSystemId? targetSystemId, CancellationToken ct = default) where TEvent : class
            => EmptyAsyncEnumerable<TEvent>.Instance;
    }

    private sealed class NoopIdempotencyStore : IIdempotencyStore
    {
        public Task<IdempotencyMatch?> FindAsync(
            SystemId principalId,
            OperationId operationId,
            IdempotencyKey idempotencyKey,
            CancellationToken cancellationToken = default) => Task.FromResult<IdempotencyMatch?>(null);

        public Task SaveAsync(
            SystemId principalId,
            OperationId operationId,
            IdempotencyKey idempotencyKey,
            string payloadHash,
            string outcomeHash,
            string? outcomePayload,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyAsyncEnumerable<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
    {
        public static readonly EmptyAsyncEnumerable<T> Instance = new();
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public T Current => default!;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
