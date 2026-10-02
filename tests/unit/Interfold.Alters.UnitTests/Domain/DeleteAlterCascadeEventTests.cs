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
using TUnit.Mocks;
using TUnit.Mocks.Arguments;

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

        var repo = IAlterRepository.Mock();
        repo.ExistsAsync(Arg.Any<SystemId>(), Arg.Any<AlterId>(), Arg.Any<CancellationToken>()).Returns(true);
        var deletion = IAlterDeletion.Mock();
        deletion.DeleteAsync(Arg.Any<SystemId>(), Arg.Any<AlterId>(), Arg.Any<CancellationToken>()).Returns(
            new AlterDeletionResult(
                new FrontAlterRemoval([frontId], HadActiveFront: true, PrimaryCleared: true),
                [tagId],
                new JournalAlterCascadeResult([journalId], [globalId]),
                [pollId]));
        var bus = IClusterEventBus.Mock();
        var handler = new DeleteAlterCommandHandler(repo.Object, deletion.Object, IdleStore(), bus.Object);

        var result = await handler.HandleAsync(Envelope(alterId));

        var published = Published(bus);
        await Assert.That(result.Accepted).IsTrue();
        await Assert.That(published).IsEquivalentTo(new object[]
        {
            new FrontingPrimaryChangedEvent(Principal, null),
            new FrontingStateChangedEvent(Principal),
            new FrontingEndedEvent(Principal, alterId),
            new FrontDeletedEvent(Principal, frontId),
            new TagUpdatedEvent(Principal, tagId),
            new AlterJournalEntryDeletedEvent(Principal, journalId),
            new GlobalJournalEntryUpdatedEvent(Principal, globalId),
            new PollUpdatedEvent(Principal, pollId),
            new AlterDeletedEvent(Principal, alterId),
        });
        await Assert.That(published[0]).IsTypeOf<FrontingPrimaryChangedEvent>();
        await Assert.That(published[^1]).IsTypeOf<AlterDeletedEvent>();
    }

    [Test]
    public async Task DeleteAlter_MissingAlter_DoesNotPublish()
    {
        var repo = IAlterRepository.Mock();
        repo.ExistsAsync(Arg.Any<SystemId>(), Arg.Any<AlterId>(), Arg.Any<CancellationToken>()).Returns(false);
        var deletion = IAlterDeletion.Mock();
        var bus = IClusterEventBus.Mock();
        var handler = new DeleteAlterCommandHandler(repo.Object, deletion.Object, IdleStore(), bus.Object);

        var result = await handler.HandleAsync(Envelope(new AlterId(4)));

        await Assert.That(result.Accepted).IsFalse();
        await Assert.That(Published(bus)).IsEmpty();
        await Assert.That(Calls(deletion, nameof(IAlterDeletion.DeleteAsync))).IsEqualTo(0);
    }

    private static CommandEnvelope<DeleteAlterCommand> Envelope(AlterId alterId) =>
        new(
            OperationId: OperationIds.AlterDelete,
            CommandId: Guid.NewGuid(),
            PrincipalId: Principal,
            IdempotencyKey: new IdempotencyKey(Guid.NewGuid().ToString("N")),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAlterCommand(alterId));

    private static IIdempotencyStore IdleStore()
    {
        var store = IIdempotencyStore.Mock();
        store.FindAsync(
                Arg.Any<SystemId>(),
                Arg.Any<OperationId>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((IdempotencyMatch?)null);
        return store.Object;
    }

    private static object[] Published(Mock<IClusterEventBus> bus) =>
        Mock.Invocations(bus)
            .Where(call => call.MemberName == nameof(IClusterEventBus.PublishAsync))
            .Select(call => call.Arguments[0]!)
            .ToArray();

    private static int Calls<T>(Mock<T> mock, string member) where T : class =>
        Mock.Invocations(mock).Count(call => call.MemberName == member);
}
