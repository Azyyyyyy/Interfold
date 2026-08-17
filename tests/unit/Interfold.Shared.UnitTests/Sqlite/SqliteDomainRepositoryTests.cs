using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Sqlite;
using Interfold.Infrastructure.Sqlite.Repository;
using Interfold.Polls.Contracts.Models.Commands;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.Api.UnitTests.Sqlite;

public sealed class SqliteFriendshipRepositoryTests
{
    [Test]
    public async Task Send_Accept_List_SetTrusted_Remove()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var repo = new SqliteFriendshipRepository(SqliteTestDb.Factory(path));
            var alice = new SystemId("alice01");
            var bob = new SystemId("bob0001");

            await Assert.That(await repo.SendRequestAsync(alice, bob)).IsEqualTo(SendFriendRequestOutcome.Sent);
            await Assert.That(await repo.SendRequestAsync(alice, bob)).IsEqualTo(SendFriendRequestOutcome.AlreadySent);

            await Assert.That(await repo.AcceptRequestAsync(bob, alice)).IsEqualTo(FriendRequestMutationOutcome.Ok);

            var friends = await repo.ListFriendshipsAsync(alice);
            await Assert.That(friends.Count).IsEqualTo(1);
            await Assert.That(friends[0].Friend.Id.Value).IsEqualTo(bob.Value);
            await Assert.That(friends[0].Friendship.Level).IsEqualTo(FriendshipLevel.Friend);

            await Assert.That(await repo.SetTrustedAsync(alice, bob, trusted: true)).IsTrue();
            var level = await repo.GetFriendshipLevelAsync(alice, bob);
            await Assert.That(level).IsEqualTo(FriendshipLevel.TrustedFriend);

            await Assert.That(await repo.RemoveFriendshipAsync(alice, bob)).IsTrue();
            await Assert.That(await repo.GetFriendshipAsync(alice, bob)).IsNull();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }

    [Test]
    public async Task MutualRequests_AutoAccept()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var repo = new SqliteFriendshipRepository(SqliteTestDb.Factory(path));
            var alice = new SystemId("alice02");
            var bob = new SystemId("bob0002");

            await Assert.That(await repo.SendRequestAsync(alice, bob)).IsEqualTo(SendFriendRequestOutcome.Sent);
            await Assert.That(await repo.SendRequestAsync(bob, alice)).IsEqualTo(SendFriendRequestOutcome.Accepted);
            await Assert.That(await repo.GetFriendshipAsync(alice, bob)).IsNotNull();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

public sealed class SqliteFrontingRepositoryTests
{
    [Test]
    public async Task Start_End_History_Primary()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var factory = SqliteTestDb.Factory(path);
            var region = new SqliteRegionContext(ScyllaKeyspace.Nam);
            IFriendshipRepository friendships = new SqliteFriendshipRepository(factory);
            IAlterRepository alters = new StubAlterRepository();
            IFrontingRepository repo = new SqliteFrontingRepository(
                factory, region, friendships, alters, NullLogger<SqliteFrontingRepository>.Instance);

            var systemId = new SystemId("front01");
            var alterId = new AlterId(1);
            var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

            var frontId = await repo.StartAsync(systemId, alterId, "hello", startedAt);
            await Assert.That(frontId).IsNotNull();
            await Assert.That(await repo.IsFrontingAsync(systemId, alterId)).IsTrue();
            await Assert.That(await repo.StartAsync(systemId, alterId, null, startedAt)).IsNull();

            await Assert.That(await repo.SetPrimaryAsync(systemId, alterId)).IsTrue();
            var active = await repo.ListActiveAsync(systemId);
            await Assert.That(active.Count).IsEqualTo(1);
            await Assert.That(active[0].Primary).IsTrue();
            await Assert.That(active[0].Front.Comment).IsEqualTo("hello");

            await Assert.That(await repo.UpdateCommentByFrontIdAsync(systemId, frontId!.Value, "updated")).IsTrue();

            var endedAt = DateTimeOffset.UtcNow;
            await Assert.That(await repo.EndAsync(systemId, alterId, endedAt)).IsTrue();
            await Assert.That(await repo.IsFrontingAsync(systemId, alterId)).IsFalse();

            var history = await repo.ListAllAsync(systemId);
            await Assert.That(history.Count).IsEqualTo(1);
            await Assert.That(history[0].Comment).IsEqualTo("updated");
            await Assert.That(history[0].TimeEnd).IsNotNull();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

public sealed class SqlitePollRepositoryTests
{
    [Test]
    public async Task Create_Update_List_Delete()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var region = new SqliteRegionContext(ScyllaKeyspace.Nam);
            IPollRepository repo = new SqlitePollRepository(SqliteTestDb.Factory(path), region);
            var systemId = new SystemId("poll001");

            var id = await repo.CreateAsync(
                systemId,
                new CreatePollCommand("Title", "Desc", PollType.Vote, null, DateTime.UtcNow));
            await Assert.That(id).IsNotNull();
            await Assert.That(await repo.ExistsAsync(systemId, id!.Value)).IsTrue();

            await Assert.That(await repo.UpdateAsync(
                systemId,
                new UpdatePollCommand(id.Value, "New title", null, null, false, null))).IsTrue();

            var got = await repo.GetAsync(systemId, id.Value);
            await Assert.That(got).IsNotNull();
            await Assert.That(got!.Title).IsEqualTo("New title");
            await Assert.That(got.Type).IsEqualTo(PollType.Vote);

            var list = await repo.ListAsync(systemId);
            await Assert.That(list.Count).IsEqualTo(1);

            await Assert.That(await repo.DeleteAsync(systemId, id.Value)).IsTrue();
            await Assert.That(await repo.ExistsAsync(systemId, id.Value)).IsFalse();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

/// <summary>Minimal alter stub so fronting ListActive can hydrate BareAlter without the full alter table.</summary>
file sealed class StubAlterRepository : IAlterRepository
{
    public Task<AlterId?> CreateAsync(SystemId systemId, CreateAlterCommand command, CancellationToken cancellationToken = default)
        => Task.FromResult<AlterId?>(null);

    public Task<bool> ExistsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> UpdateAsync(SystemId systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> DeleteAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<IReadOnlyList<AlterReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AlterReadModel>>(Array.Empty<AlterReadModel>());

    public Task<IReadOnlyList<BareAlter>> ListGuardedAsync(
        SystemId systemId, SystemId? viewerSystemId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BareAlter>>(Array.Empty<BareAlter>());

    public Task<AlterReadModel?> GetAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
        => Task.FromResult<AlterReadModel?>(null);

    public Task<BareAlter?> GetGuardedAsync(
        SystemId systemId, AlterId alterId, SystemId? viewerSystemId, CancellationToken cancellationToken = default)
        => Task.FromResult<BareAlter?>(null);

    public Task<bool> AliasTakenByOtherAsync(
        SystemId systemId, AlterId alterId, string alias, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
