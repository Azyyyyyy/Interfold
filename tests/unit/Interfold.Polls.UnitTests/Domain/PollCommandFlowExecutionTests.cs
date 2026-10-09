using Interfold.Polls.Contracts.Ids;
using Interfold.Polls.Contracts.Models.Commands;
using Interfold.Polls.Domain;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using TUnit.Mocks;
using TUnit.Mocks.Arguments;

namespace Interfold.Api.UnitTests.Domain;

// Regression harness for PollCommandFlow.ExecuteExistingPollMutationAsync. Guards
// against a prior eager-Task footgun where UpdateAsync/DeleteAsync and PublishAsync
// fired against a not-found row before ExistsAsync completed — invisible to HTTP-level
// tests but observable in a spurious write + published event. Helper now takes
// Func<CT, ...>; these tests count calls to catch a re-regression at the seam.
public sealed class PollCommandFlowExecutionTests
{
    private static readonly SystemId Principal =
        new SystemId("poll-flow-tests");

    private static readonly PollId AnyPollId = new(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    [Test]
    public async Task DeletePoll_MissingPoll_DoesNotMutateOrPublish()
    {
        var (repo, bus, handler) = DeleteHarness(exists: false);

        var result = await handler.HandleAsync(NewEnvelope(OperationIds.PollDelete, new DeletePollCommand(AnyPollId)));

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsFalse()
                .Because("A missing poll must be rejected with poll:not_found, not accepted.");
            await Assert.That(result.Conflict?.EntityRef).IsEqualTo(EntityRefs.PollNotFound)
                .Because("The rejection must be the not-found invariant, not a mutation-failed variant.");
            await Assert.That(Calls(repo, nameof(IPollRepository.DeleteAsync))).IsEqualTo(0)
                .Because("A not-found poll must never reach DeleteAsync — the eager-Task footgun would raise this count.");
            await Assert.That(Calls(bus, nameof(IClusterEventBus.PublishAsync))).IsEqualTo(0)
                .Because("A not-found poll must never publish PollDeletedEvent — the eager-Task footgun would raise this count.");
            await Assert.That(Calls(repo, nameof(IPollRepository.ExistsAsync))).IsEqualTo(1)
                .Because("The existence check itself must run exactly once so the reject path is provable.");
        }
    }

    [Test]
    public async Task DeletePoll_ExistsButMutationReturnsFalse_RejectsWithDeleteFailedAndDoesNotPublish()
    {
        var (repo, bus, handler) = DeleteHarness(exists: true, deleteResult: false);

        var result = await handler.HandleAsync(NewEnvelope(OperationIds.PollDelete, new DeletePollCommand(AnyPollId)));

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsFalse();
            await Assert.That(result.Conflict?.EntityRef).IsEqualTo(EntityRefs.PollDeleteFailed);
            await Assert.That(Calls(repo, nameof(IPollRepository.DeleteAsync))).IsEqualTo(1);
            await Assert.That(Calls(bus, nameof(IClusterEventBus.PublishAsync))).IsEqualTo(0)
                .Because("A mutation that reports no change must not surface a Deleted event.");
        }
    }

    [Test]
    public async Task DeletePoll_HappyPath_CallsMutateOncePublishesOnceAndAccepts()
    {
        var (repo, bus, handler) = DeleteHarness(exists: true, deleteResult: true);

        var result = await handler.HandleAsync(NewEnvelope(OperationIds.PollDelete, new DeletePollCommand(AnyPollId)));

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsTrue();
            await Assert.That(Calls(repo, nameof(IPollRepository.DeleteAsync))).IsEqualTo(1);
            await Assert.That(Calls(bus, nameof(IClusterEventBus.PublishAsync))).IsEqualTo(1);
        }
    }

    // Update shares the helper with Delete so it inherits the same footgun; pinned
    // separately so drift in one branch cannot silently pass in the other.
    [Test]
    public async Task UpdatePoll_MissingPoll_DoesNotMutateOrPublish()
    {
        var repo = IPollRepository.Mock();
        repo.ExistsAsync(Arg.Any<SystemId>(), Arg.Any<PollId>(), Arg.Any<CancellationToken>()).Returns(false);
        repo.UpdateAsync(Arg.Any<SystemId>(), Arg.Any<UpdatePollCommand>(), Arg.Any<CancellationToken>()).Returns(false);
        var bus = IClusterEventBus.Mock();
        var handler = new UpdatePollCommandHandler(repo.Object, IdleStore(), bus.Object);

        // Populate one mutable field so the handler doesn't short-circuit at
        // PollCommandValidation.HasNoMutableFields.
        var payload = new UpdatePollCommand(
            Id: AnyPollId,
            Title: "renamed",
            Description: null,
            TimeEnd: null,
            HasTimeEnd: false,
            Data: null);
        var result = await handler.HandleAsync(NewEnvelope(OperationIds.PollUpdate, payload));

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsFalse();
            await Assert.That(result.Conflict?.EntityRef).IsEqualTo(EntityRefs.PollNotFound);
            await Assert.That(Calls(repo, nameof(IPollRepository.UpdateAsync))).IsEqualTo(0)
                .Because("A not-found poll must never reach UpdateAsync — the eager-Task footgun would raise this count.");
            await Assert.That(Calls(bus, nameof(IClusterEventBus.PublishAsync))).IsEqualTo(0)
                .Because("A not-found poll must never publish PollUpdatedEvent — the eager-Task footgun would raise this count.");
        }
    }

    private static CommandEnvelope<T> NewEnvelope<T>(OperationId opId, T payload) =>
        new(
            OperationId: opId,
            CommandId: Guid.NewGuid(),
            PrincipalId: Principal,
            IdempotencyKey: new IdempotencyKey(Guid.NewGuid().ToString("N")),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload);

    private static (Mock<IPollRepository> Repo, Mock<IClusterEventBus> Bus, DeletePollCommandHandler Handler) DeleteHarness(
        bool exists,
        bool deleteResult = false)
    {
        var repo = IPollRepository.Mock();
        repo.ExistsAsync(Arg.Any<SystemId>(), Arg.Any<PollId>(), Arg.Any<CancellationToken>()).Returns(exists);
        repo.DeleteAsync(Arg.Any<SystemId>(), Arg.Any<PollId>(), Arg.Any<CancellationToken>()).Returns(deleteResult);
        var bus = IClusterEventBus.Mock();
        var handler = new DeletePollCommandHandler(repo.Object, IdleStore(), bus.Object);
        return (repo, bus, handler);
    }

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

    private static int Calls<T>(Mock<T> mock, string member) where T : class =>
        Mock.Invocations(mock).Count(call => call.MemberName == member);
}

