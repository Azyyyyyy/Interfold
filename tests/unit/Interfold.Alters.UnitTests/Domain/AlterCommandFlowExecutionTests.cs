using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Alters.Domain;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using TUnit.Mocks;
using TUnit.Mocks.Arguments;

namespace Interfold.Api.UnitTests.Domain;

// Regression harness for UpdateAlterCommandHandler's reject branches. Same "no side
// effects when validation fails" contract PollCommandFlowExecutionTests pins for the
// poll flow — a future eager-argument evaluation would spuriously write + publish
// while HTTP-level tests still see the correct alter:not_found conflict.
public sealed class AlterCommandFlowExecutionTests
{
    private static readonly SystemId Principal = new SystemId("altrflw");

    private static readonly AlterId AnyAlterId = new(5);

    [Test]
    public async Task UpdateAlter_MissingAlter_RejectsWithNotFoundAndDoesNotMutateOrPublish()
    {
        var (repo, bus, handler) = Harness(exists: false);

        var result = await handler.HandleAsync(NewEnvelope(NewMutatingPayload("renamed")));

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsFalse()
                .Because("A missing alter must be rejected, never accepted.");
            await Assert.That(result.Conflict?.EntityRef).IsEqualTo(EntityRefs.AlterNotFound)
                .Because("The rejection must be the not-found invariant, not a mutation-failed variant.");
            await Assert.That(Calls(repo, nameof(IAlterRepository.ExistsAsync))).IsEqualTo(1)
                .Because("The existence check itself must run exactly once so the reject path is provable.");
            await Assert.That(Calls(repo, nameof(IAlterRepository.UpdateAsync))).IsEqualTo(0)
                .Because("A not-found alter must never reach UpdateAsync — an eager-Task regression on ExecuteMutationAsync would raise this count.");
            await Assert.That(Calls(bus, nameof(IClusterEventBus.PublishAsync))).IsEqualTo(0)
                .Because("A not-found alter must never publish AlterUpdatedEvent — same eager-Task regression class.");
            await Assert.That(Calls(repo, nameof(IAlterRepository.AliasTakenByOtherAsync))).IsEqualTo(0)
                .Because("The alias-collision probe is downstream of the existence check and must not run against a missing row.");
        }
    }

    [Test]
    public async Task UpdateAlter_ExistsButMutationReturnsFalse_RejectsWithUpdateFailedAndDoesNotPublish()
    {
        var (repo, bus, handler) = Harness(exists: true, updateResult: false);

        var result = await handler.HandleAsync(NewEnvelope(NewMutatingPayload("renamed")));

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsFalse();
            await Assert.That(result.Conflict?.EntityRef).IsEqualTo(EntityRefs.AlterUpdateFailed);
            await Assert.That(Calls(repo, nameof(IAlterRepository.UpdateAsync))).IsEqualTo(1);
            await Assert.That(Calls(bus, nameof(IClusterEventBus.PublishAsync))).IsEqualTo(0)
                .Because("A mutation that reports no change must not surface an Updated event.");
        }
    }

    [Test]
    public async Task UpdateAlter_HappyPath_CallsMutateOncePublishesOnceAndAccepts()
    {
        var (repo, bus, handler) = Harness(exists: true, updateResult: true);

        var result = await handler.HandleAsync(NewEnvelope(NewMutatingPayload("renamed")));

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsTrue();
            await Assert.That(Calls(repo, nameof(IAlterRepository.UpdateAsync))).IsEqualTo(1);
            await Assert.That(Calls(bus, nameof(IClusterEventBus.PublishAsync))).IsEqualTo(1);
        }
    }

    private static UpdateAlterCommand NewMutatingPayload(string newName) =>
        new()
        {
            AlterId = AnyAlterId,
            Name = newName,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static CommandEnvelope<UpdateAlterCommand> NewEnvelope(UpdateAlterCommand payload) =>
        new(
            OperationId: OperationIds.AlterUpdate,
            CommandId: Guid.NewGuid(),
            PrincipalId: Principal,
            IdempotencyKey: new IdempotencyKey(Guid.NewGuid().ToString("N")),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload);

    private static (Mock<IAlterRepository> Repo, Mock<IClusterEventBus> Bus, UpdateAlterCommandHandler Handler) Harness(
        bool exists,
        bool updateResult = false)
    {
        var repo = IAlterRepository.Mock();
        repo.ExistsAsync(Arg.Any<SystemId>(), Arg.Any<AlterId>(), Arg.Any<CancellationToken>()).Returns(exists);
        repo.UpdateAsync(Arg.Any<SystemId>(), Arg.Any<UpdateAlterCommand>(), Arg.Any<CancellationToken>()).Returns(updateResult);
        repo.AliasTakenByOtherAsync(Arg.Any<SystemId>(), Arg.Any<AlterId>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        var bus = IClusterEventBus.Mock();
        var handler = new UpdateAlterCommandHandler(repo.Object, IdleStore(), bus.Object);
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
