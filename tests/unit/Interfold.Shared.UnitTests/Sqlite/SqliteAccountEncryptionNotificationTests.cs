using Interfold.Auth.Contracts.Ids;
using Interfold.Friendships.Contracts.Ids;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Settings.Contracts.Ids;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Infrastructure.Sqlite;
using Interfold.Infrastructure.Sqlite.Repository;

namespace Interfold.Api.UnitTests.Sqlite;

public sealed class SqliteEncryptionStateRepositoryTests
{
    [Test]
    public async Task Upsert_Then_Get_RoundTrip_PreservesSaltOnOmit()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var repo = new SqliteEncryptionStateRepository(SqliteTestDb.Factory(path));
            var systemId = new SystemId("alice");
            var salt = EncryptionSalt.NewRandom();

            await repo.UpsertAsync(systemId, initialized: false, keyChecksum: null, salt);
            var first = await repo.GetAsync(systemId);
            await Assert.That(first).IsNotNull();
            await Assert.That(first!.Initialized).IsFalse();
            await Assert.That(first.Salt).IsEqualTo(salt);

            await repo.UpsertAsync(systemId, initialized: true, keyChecksum: new KeyChecksum("chk"), salt: null);
            var second = await repo.GetAsync(systemId);
            await Assert.That(second!.Initialized).IsTrue();
            await Assert.That(second.KeyChecksum).IsEqualTo(new KeyChecksum("chk"));
            await Assert.That(second.Salt).IsEqualTo(salt);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

public sealed class SqliteAccountRepositoryTests
{
    [Test]
    public async Task Username_Profile_And_LinkToken_RoundTrip()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var region = new SqliteRegionContext(ScyllaKeyspace.Nam);
            var encryption = new SqliteEncryptionStateRepository(SqliteTestDb.Factory(path));
            var repo = new SqliteAccountRepository(SqliteTestDb.Factory(path), region, encryption);
            var systemId = new SystemId("bob");

            await repo.UpdateUsernameAsync(systemId, new Username("bobbie"));
            var profile = await repo.GetPublicProfileAsync(systemId);
            await Assert.That(profile).IsNotNull();
            await Assert.That(profile!.Username?.Value).IsEqualTo("bobbie");

            var token = await repo.GetOrCreateLinkTokenAsync(systemId);
            await Assert.That(token.Value).IsNotEmpty();
            var got = await repo.GetLinkTokenAsync(systemId);
            await Assert.That(got).IsEqualTo(token);

            var resolved = await repo.ResolveSystemIdByLinkTokenAsync(token);
            await Assert.That(resolved).IsNotNull();
            await Assert.That(ScopedSystemId.StripRegionPrefix(resolved!.Value))
                .IsEqualTo("bob");
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }

    [Test]
    public async Task FindOrCreate_Discord_SeedsEncryptionSalt()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var region = new SqliteRegionContext(ScyllaKeyspace.Nam);
            var encryption = new SqliteEncryptionStateRepository(SqliteTestDb.Factory(path));
            var repo = new SqliteAccountRepository(SqliteTestDb.Factory(path), region, encryption);

            var created = await repo.FindOrCreateSystemIdAsync(
                ProviderIdentity.FromDiscord(new DiscordId("discord-123")));
            await Assert.That(created).IsNotNull();

            var state = await encryption.GetAsync(created!.Value);
            await Assert.That(state).IsNotNull();
            await Assert.That(state!.Salt).IsNotNull();

            var again = await repo.FindOrCreateSystemIdAsync(
                ProviderIdentity.FromDiscord(new DiscordId("discord-123")));
            await Assert.That(again).IsEqualTo(created);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

public sealed class SqliteNotificationTokenRepositoryTests
{
    [Test]
    public async Task Add_Then_ListForFriends_Then_Remove()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var me = new SystemId("me");
            var friend = new SystemId("friend");
            var friendships = new FixedFriendshipRepository(me, friend);
            var repo = new SqliteNotificationTokenRepository(SqliteTestDb.Factory(path), friendships);

            var token = new PushToken("device-token-1");
            await repo.AddAsync(friend, token);

            var groups = await repo.ListTokensForFriendsOfAsync(me);
            await Assert.That(groups.Count).IsEqualTo(1);
            await Assert.That(groups[0].FriendSystemId.Value).IsEqualTo(friend.Value);
            await Assert.That(groups[0].Tokens.Count).IsEqualTo(1);
            await Assert.That(groups[0].Tokens[0].Value).IsEqualTo(token.Value);

            await repo.RemoveAsync(token);
            var after = await repo.ListTokensForFriendsOfAsync(me);
            await Assert.That(after.Count).IsEqualTo(0);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }

    private sealed class FixedFriendshipRepository(SystemId owner, SystemId friend) : IFriendshipRepository
    {
        public Task<IReadOnlyList<FriendshipReadModel>> ListFriendshipsAsync(
            SystemId systemId,
            CancellationToken cancellationToken = default)
        {
            if (systemId.Value != owner.Value)
                return Task.FromResult<IReadOnlyList<FriendshipReadModel>>(Array.Empty<FriendshipReadModel>());

            var profile = new FriendProfileReadModel(friend, null, null, null, null, null);
            var model = new FriendshipReadModel(
                profile,
                new FriendshipModel(FriendshipLevel.Friend, DateTimeOffset.UtcNow),
                Array.Empty<FriendFrontingReadModel>());
            return Task.FromResult<IReadOnlyList<FriendshipReadModel>>([model]);
        }

        public Task<SystemId?> ResolveUserIdAsync(FriendLookup lookup, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<FriendshipLevel?> GetFriendshipLevelAsync(SystemId systemId, SystemId? viewerSystemId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<FriendshipReadModel?> GetFriendshipAsync(SystemId systemId, SystemId friendSystemId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RemoveFriendshipAsync(SystemId systemId, SystemId friendSystemId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> SetTrustedAsync(SystemId systemId, SystemId friendSystemId, bool trusted, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(SystemId systemId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<SendFriendRequestOutcome> SendRequestAsync(SystemId systemId, SystemId targetSystemId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<FriendRequestMutationOutcome> AcceptRequestAsync(SystemId systemId, SystemId sourceSystemId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<FriendRequestMutationOutcome> RejectRequestAsync(SystemId systemId, SystemId sourceSystemId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<FriendRequestMutationOutcome> CancelRequestAsync(SystemId systemId, SystemId targetSystemId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<SystemId>> DeleteAllForSystemAsync(SystemId systemId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
