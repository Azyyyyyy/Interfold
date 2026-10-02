using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Friendships.Contracts.Ids;
using Interfold.Alters.Domain;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Sqlite;
using Interfold.Infrastructure.Sqlite.Repository;
using Interfold.Journals.Domain;
using Interfold.Polls.Contracts.Models.Commands;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Settings.Domain;
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
            var repo = new SqliteFriendshipRepository(SqliteTestDb.Factory(path), TimeProvider.System);
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
            var repo = new SqliteFriendshipRepository(SqliteTestDb.Factory(path), TimeProvider.System);
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

    [Test]
    public async Task ResolveUserId_Username_UsesAccountRepository()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var factory = SqliteTestDb.Factory(path);
            var encryption = new SqliteEncryptionStateRepository(factory, TimeProvider.System);
            var accounts = new SqliteAccountRepository(factory, encryption, TimeProvider.System);
            var friendships = new SqliteFriendshipRepository(factory, TimeProvider.System, accounts);
            var systemId = new SystemId("nameusr");
            await accounts.EnsureExistsAsync(systemId);
            await accounts.UpdateUsernameAsync(systemId, new Username("Casey"));

            var resolved = await friendships.ResolveUserIdAsync(FriendLookup.Parse("username:casey", provider: null));
            await Assert.That(resolved).IsEqualTo(systemId);
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
            var friendships = new SqliteFriendshipRepository(factory, TimeProvider.System);
            var settings = new SqliteSettingsFieldRepository(factory, TimeProvider.System);
            var alterFields = new AlterFieldDefinitionsAdapter(settings, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
            IAlterRepository alters = new SqliteAlterRepository(
                factory, friendships, settings, alterFields,
                NullLogger<SqliteAlterRepository>.Instance);
            IFrontingRepository repo = new SqliteFrontingRepository(
                factory, friendships, alters, NullLogger<SqliteFrontingRepository>.Instance, TimeProvider.System);

            var systemId = new SystemId("front01");
            var alterId = await alters.CreateAsync(systemId, new CreateAlterCommand("FrontAlter", DateTimeOffset.UtcNow));
            await Assert.That(alterId).IsNotNull();

            var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

            var frontId = await repo.StartAsync(systemId, alterId!.Value, "hello", startedAt);
            await Assert.That(frontId).IsNotNull();
            await Assert.That(await repo.IsFrontingAsync(systemId, alterId.Value)).IsTrue();
            await Assert.That(await repo.StartAsync(systemId, alterId.Value, null, startedAt)).IsNull();

            await Assert.That(await repo.SetPrimaryAsync(systemId, alterId.Value)).IsTrue();
            var active = await repo.ListActiveAsync(systemId);
            await Assert.That(active.Count).IsEqualTo(1);
            await Assert.That(active[0].Primary).IsTrue();
            await Assert.That(active[0].Front.Comment).IsEqualTo("hello");
            await Assert.That(active[0].Alter.Name).IsEqualTo("FrontAlter");

            await Assert.That(await repo.UpdateCommentByFrontIdAsync(systemId, frontId!.Value, "updated")).IsTrue();

            var endedAt = DateTimeOffset.UtcNow;
            await Assert.That(await repo.EndAsync(systemId, alterId.Value, endedAt)).IsTrue();
            await Assert.That(await repo.IsFrontingAsync(systemId, alterId.Value)).IsFalse();

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

    [Test]
    public async Task DeleteAlter_ClearsActiveFront_SoListActiveSucceeds()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var factory = SqliteTestDb.Factory(path);
            var friendships = new SqliteFriendshipRepository(factory, TimeProvider.System);
            var settings = new SqliteSettingsFieldRepository(factory, TimeProvider.System);
            var alterFields = new AlterFieldDefinitionsAdapter(settings, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
            IAlterRepository alters = new SqliteAlterRepository(
                factory, friendships, settings, alterFields,
                NullLogger<SqliteAlterRepository>.Instance);
            IFrontingRepository fronts = new SqliteFrontingRepository(
                factory, friendships, alters, NullLogger<SqliteFrontingRepository>.Instance, TimeProvider.System);

            var systemId = new SystemId("wipe001");
            var kept = await alters.CreateAsync(systemId, new CreateAlterCommand("Kept", DateTimeOffset.UtcNow));
            var wiped = await alters.CreateAsync(systemId, new CreateAlterCommand("Wiped", DateTimeOffset.UtcNow));
            await Assert.That(kept).IsNotNull();
            await Assert.That(wiped).IsNotNull();

            var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await fronts.StartAsync(systemId, kept!.Value, null, startedAt);
            await fronts.StartAsync(systemId, wiped!.Value, "fronting", startedAt);
            await Assert.That(await fronts.SetPrimaryAsync(systemId, wiped.Value)).IsTrue();

            var polls = new SqlitePollRepository(factory, TimeProvider.System);
            var journals = new SqliteJournalRepository(factory, TimeProvider.System);
            var tags = new SqliteTagRepository(
                factory, friendships, alters, NullLogger<SqliteTagRepository>.Instance, TimeProvider.System);
            var deletion = new AlterDeletion(
                new SqliteStorageTransactionFactory(factory),
                fronts,
                tags,
                new JournalAlterCascadeAdapter(journals),
                polls,
                alters);

            var deleted = await deletion.DeleteAsync(systemId, wiped.Value);
            await Assert.That(deleted).IsNotNull();
            await Assert.That(deleted!.Fronts.DeletedFrontIds.Count).IsEqualTo(1);
            await Assert.That(deleted.Fronts.HadActiveFront).IsTrue();
            await Assert.That(deleted.Fronts.PrimaryCleared).IsTrue();

            var active = await fronts.ListActiveAsync(systemId);
            await Assert.That(active.Count).IsEqualTo(1);
            await Assert.That(active[0].Alter.Id).IsEqualTo(kept.Value);
            await Assert.That(active[0].Primary).IsFalse();

            var history = await fronts.ListAllAsync(systemId);
            await Assert.That(history.Count).IsEqualTo(1);
            await Assert.That(history[0].AlterId).IsEqualTo(kept.Value);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }

    [Test]
    public async Task UncommittedFrontDelete_LeavesHistoryInPlace()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var factory = SqliteTestDb.Factory(path);
            var friendships = new SqliteFriendshipRepository(factory, TimeProvider.System);
            var settings = new SqliteSettingsFieldRepository(factory, TimeProvider.System);
            var alterFields = new AlterFieldDefinitionsAdapter(settings, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
            IAlterRepository alters = new SqliteAlterRepository(
                factory, friendships, settings, alterFields,
                NullLogger<SqliteAlterRepository>.Instance);
            IFrontingRepository fronts = new SqliteFrontingRepository(
                factory, friendships, alters, NullLogger<SqliteFrontingRepository>.Instance, TimeProvider.System);

            var systemId = new SystemId("wipe002");
            var alterId = await alters.CreateAsync(systemId, new CreateAlterCommand("StillHere", DateTimeOffset.UtcNow));
            await Assert.That(alterId).IsNotNull();
            await fronts.StartAsync(systemId, alterId!.Value, "fronting", DateTimeOffset.UtcNow.AddMinutes(-5));

            var transactions = new SqliteStorageTransactionFactory(factory);
            await using (var transaction = await transactions.BeginAsync())
            {
                await fronts.DeleteAllForAlterAsync(systemId, alterId.Value);
            }

            var history = await fronts.ListAllAsync(systemId);
            await Assert.That(history.Count).IsEqualTo(1);
            await Assert.That(history[0].AlterId).IsEqualTo(alterId.Value);
            await Assert.That(await alters.ExistsAsync(systemId, alterId.Value)).IsTrue();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }

    [Test]
    public async Task UncommittedAlterDeletion_LeavesAlterAndFront()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var factory = SqliteTestDb.Factory(path);
            var friendships = new SqliteFriendshipRepository(factory, TimeProvider.System);
            var settings = new SqliteSettingsFieldRepository(factory, TimeProvider.System);
            var alterFields = new AlterFieldDefinitionsAdapter(settings, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
            IAlterRepository alters = new SqliteAlterRepository(
                factory, friendships, settings, alterFields,
                NullLogger<SqliteAlterRepository>.Instance);
            IFrontingRepository fronts = new SqliteFrontingRepository(
                factory, friendships, alters, NullLogger<SqliteFrontingRepository>.Instance, TimeProvider.System);

            var systemId = new SystemId("wipe003");
            var alterId = await alters.CreateAsync(systemId, new CreateAlterCommand("StillHere", DateTimeOffset.UtcNow));
            await Assert.That(alterId).IsNotNull();
            await fronts.StartAsync(systemId, alterId!.Value, "fronting", DateTimeOffset.UtcNow.AddMinutes(-5));

            var polls = new SqlitePollRepository(factory, TimeProvider.System);
            var journals = new SqliteJournalRepository(factory, TimeProvider.System);
            var tags = new SqliteTagRepository(
                factory, friendships, alters, NullLogger<SqliteTagRepository>.Instance, TimeProvider.System);
            var deletion = new AlterDeletion(
                new SqliteStorageTransactionFactory(factory),
                fronts,
                tags,
                new JournalAlterCascadeAdapter(journals),
                polls,
                alters);

            var transactions = new SqliteStorageTransactionFactory(factory);
            await using (var transaction = await transactions.BeginAsync())
            {
                var deleted = await deletion.DeleteAsync(systemId, alterId.Value);
                await Assert.That(deleted).IsNotNull();
            }

            await Assert.That(await alters.ExistsAsync(systemId, alterId.Value)).IsTrue();
            var history = await fronts.ListAllAsync(systemId);
            await Assert.That(history.Count).IsEqualTo(1);
            await Assert.That(history[0].AlterId).IsEqualTo(alterId.Value);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }

    [Test]
    public async Task UncommittedFrontEndAndDelete_LeavesActiveFront()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var factory = SqliteTestDb.Factory(path);
            var friendships = new SqliteFriendshipRepository(factory, TimeProvider.System);
            var settings = new SqliteSettingsFieldRepository(factory, TimeProvider.System);
            var alterFields = new AlterFieldDefinitionsAdapter(settings, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
            IAlterRepository alters = new SqliteAlterRepository(
                factory, friendships, settings, alterFields,
                NullLogger<SqliteAlterRepository>.Instance);
            IFrontingRepository fronts = new SqliteFrontingRepository(
                factory, friendships, alters, NullLogger<SqliteFrontingRepository>.Instance, TimeProvider.System);

            var systemId = new SystemId("front02");
            var alterId = await alters.CreateAsync(systemId, new CreateAlterCommand("FrontAlter", DateTimeOffset.UtcNow));
            await Assert.That(alterId).IsNotNull();
            var frontId = await fronts.StartAsync(systemId, alterId!.Value, "fronting", DateTimeOffset.UtcNow.AddMinutes(-5));
            await Assert.That(frontId).IsNotNull();

            var transactions = new SqliteStorageTransactionFactory(factory);
            await using (var transaction = await transactions.BeginAsync())
            {
                await Assert.That(await fronts.EndAsync(systemId, alterId.Value, DateTimeOffset.UtcNow)).IsTrue();
                await Assert.That(await fronts.DeleteFrontByIdAsync(systemId, frontId!.Value)).IsTrue();
            }

            await Assert.That(await fronts.IsFrontingAsync(systemId, alterId.Value)).IsTrue();
            await Assert.That((await fronts.ListAllAsync(systemId)).Count).IsEqualTo(1);

            await using (var transaction = await transactions.BeginAsync())
            {
                await Assert.That(await fronts.EndAsync(systemId, alterId.Value, DateTimeOffset.UtcNow)).IsTrue();
                await Assert.That(await fronts.DeleteFrontByIdAsync(systemId, frontId.Value)).IsTrue();
                await transaction.CommitAsync();
            }

            await Assert.That(await fronts.IsFrontingAsync(systemId, alterId.Value)).IsFalse();
            await Assert.That((await fronts.ListAllAsync(systemId)).Count).IsEqualTo(0);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }

    [Test]
    public async Task UncommittedAlterWipe_LeavesEveryAlter()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var factory = SqliteTestDb.Factory(path);
            var friendships = new SqliteFriendshipRepository(factory, TimeProvider.System);
            var settings = new SqliteSettingsFieldRepository(factory, TimeProvider.System);
            var alterFields = new AlterFieldDefinitionsAdapter(settings, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
            IAlterRepository alters = new SqliteAlterRepository(
                factory, friendships, settings, alterFields,
                NullLogger<SqliteAlterRepository>.Instance);
            IFrontingRepository fronts = new SqliteFrontingRepository(
                factory, friendships, alters, NullLogger<SqliteFrontingRepository>.Instance, TimeProvider.System);
            var polls = new SqlitePollRepository(factory, TimeProvider.System);
            var journals = new SqliteJournalRepository(factory, TimeProvider.System);
            var tags = new SqliteTagRepository(
                factory, friendships, alters, NullLogger<SqliteTagRepository>.Instance, TimeProvider.System);
            var deletion = new AlterDeletion(
                new SqliteStorageTransactionFactory(factory),
                fronts,
                tags,
                new JournalAlterCascadeAdapter(journals),
                polls,
                alters);

            var systemId = new SystemId("wipe004");
            var first = await alters.CreateAsync(systemId, new CreateAlterCommand("One", DateTimeOffset.UtcNow));
            var second = await alters.CreateAsync(systemId, new CreateAlterCommand("Two", DateTimeOffset.UtcNow));
            await Assert.That(first).IsNotNull();
            await Assert.That(second).IsNotNull();

            var transactions = new SqliteStorageTransactionFactory(factory);
            await using (var transaction = await transactions.BeginAsync())
            {
                await Assert.That(await deletion.DeleteAsync(systemId, first!.Value)).IsNotNull();
                await Assert.That(await deletion.DeleteAsync(systemId, second!.Value)).IsNotNull();
            }

            await Assert.That(await alters.ExistsAsync(systemId, first.Value)).IsTrue();
            await Assert.That(await alters.ExistsAsync(systemId, second.Value)).IsTrue();

            await using (var transaction = await transactions.BeginAsync())
            {
                await Assert.That(await deletion.DeleteAsync(systemId, first.Value)).IsNotNull();
                await Assert.That(await deletion.DeleteAsync(systemId, second.Value)).IsNotNull();
                await transaction.CommitAsync();
            }

            await Assert.That(await alters.ExistsAsync(systemId, first.Value)).IsFalse();
            await Assert.That(await alters.ExistsAsync(systemId, second.Value)).IsFalse();
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
            IPollRepository repo = new SqlitePollRepository(SqliteTestDb.Factory(path), TimeProvider.System);
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
