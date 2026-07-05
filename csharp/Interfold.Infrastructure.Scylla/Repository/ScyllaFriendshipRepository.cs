using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaFriendshipRepository : IFriendshipRepository
{
    // Derived from the ScyllaKeyspace enum so the region list can't drift from the typed
    // vocabulary the resolution APIs use.
    private static readonly string[] CanonicalRegions =
        Enum.GetValues<ScyllaKeyspace>().Select(EnumWireExtensions.ToWireValue).ToArray();

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaFriendshipRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceConfiguration options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
    }

    public async Task<SystemId?> ResolveUserIdAsync(UsernameOrSystemId userNameOrId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync<SystemId?>(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalized = new SystemId(_keyspaceResolver.NormalizeSystemId(userNameOrId.Value));
            return await ResolveUserIdInScyllaAsync(session, normalized);
        }, _options, cancellationToken);
    }

    public async Task<FriendshipLevel?> GetFriendshipLevelAsync(SystemId systemId, SystemId? viewerSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(viewerSystemId?.Value))
            {
                return null;
            }

            var normalizedSystemId = _keyspaceResolver.NormalizeTyped(systemId);
            var normalizedViewerSystemId = _keyspaceResolver.NormalizeTyped(viewerSystemId.Value);

            if (normalizedSystemId == normalizedViewerSystemId)
            {
                return FriendshipLevel.TrustedFriend;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var query = new SimpleStatement(
                "SELECT level FROM global.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
                normalizedSystemId.Value,
                normalizedViewerSystemId.Value);

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null ? null : (FriendshipLevel?)FriendshipLevelExtensions.FromCode(row.GetValue<short>("level"));
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<FriendshipReadModel>> ListFriendshipsAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeTyped(systemId);
            var profileHydrationConcurrency = _options.HydrationMaxConcurrency;

            var query = new SimpleStatement(
                "SELECT friend_id, level, since FROM global.friendships WHERE user_id = ?",
                normalizedSystemId.Value);

            var rows = await session.ExecuteAsync(query);
            var result = await ConcurrentProjection.SelectWithConcurrencyAsync(
                rows,
                profileHydrationConcurrency,
                async row =>
                {
                    var friendId = new SystemId(row.GetValue<string>("friend_id"));
                    var level = FriendshipLevelExtensions.FromCode(row.GetValue<short>("level"));
                    var since = row.GetValue<DateTimeOffset?>("since") ?? DateTimeOffset.UtcNow;

                    var profileTask = GetFriendProfileAsync(session, friendId);
                    var frontingTask = GetFrontingAsync(session, friendId, normalizedSystemId);
                    await Task.WhenAll(profileTask, frontingTask);

                    return new FriendshipReadModel(
                        await profileTask,
                        new FriendshipModel(level, since),
                        await frontingTask);
                },
                cancellationToken);

            return result.OrderByDescending(x => x.Friendship.Since).ToList();
        }, _options, cancellationToken);
    }

    public async Task<FriendshipReadModel?> GetFriendshipAsync(SystemId systemId, SystemId friendSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeTyped(systemId);
            var normalizedFriendSystemId = _keyspaceResolver.NormalizeTyped(friendSystemId);

            var query = new SimpleStatement(
                "SELECT friend_id, level, since FROM global.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
                normalizedSystemId.Value,
                normalizedFriendSystemId.Value);

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            var since = row.GetValue<DateTimeOffset?>("since") ?? DateTimeOffset.UtcNow;
            var level = FriendshipLevelExtensions.FromCode(row.GetValue<short>("level"));
            var profile = await GetFriendProfileAsync(session, normalizedFriendSystemId);
            var fronting = await GetFrontingAsync(session, normalizedFriendSystemId, normalizedSystemId);

            return new FriendshipReadModel(
                profile,
                new FriendshipModel(level, since),
                fronting);
        }, _options, cancellationToken);
    }

    public async Task<bool> RemoveFriendshipAsync(SystemId systemId, SystemId friendSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeTyped(systemId);
            var normalizedFriendId = _keyspaceResolver.NormalizeTyped(friendSystemId);

            var exists = await ExistsFriendshipAsync(session, normalizedSystemId, normalizedFriendId);
            if (!exists)
            {
                return false;
            }

            var removeBatch = new BatchStatement();
            removeBatch.Add(new SimpleStatement(
                "DELETE FROM global.friendships WHERE user_id = ? AND friend_id = ?",
                normalizedSystemId.Value,
                normalizedFriendId.Value));
            removeBatch.Add(new SimpleStatement(
                "DELETE FROM global.friendships WHERE user_id = ? AND friend_id = ?",
                normalizedFriendId.Value,
                normalizedSystemId.Value));
            removeBatch.Add(new SimpleStatement(
                "DELETE FROM global.friendships_by_friend_id WHERE friend_id = ? AND user_id = ?",
                normalizedFriendId.Value,
                normalizedSystemId.Value));
            removeBatch.Add(new SimpleStatement(
                "DELETE FROM global.friendships_by_friend_id WHERE friend_id = ? AND user_id = ?",
                normalizedSystemId.Value,
                normalizedFriendId.Value));
            await session.ExecuteAsync(removeBatch);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetTrustedAsync(SystemId systemId, SystemId friendSystemId, bool trusted, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeTyped(systemId);
            var normalizedFriendSystemId = _keyspaceResolver.NormalizeTyped(friendSystemId);

            var exists = await ExistsFriendshipAsync(session, normalizedSystemId, normalizedFriendSystemId);
            if (!exists)
            {
                return false;
            }

            var batch = new BatchStatement();
            batch.Add(new SimpleStatement(
                "UPDATE global.friendships SET level = ? WHERE user_id = ? AND friend_id = ?",
                (trusted ? FriendshipLevel.TrustedFriend : FriendshipLevel.Friend).ToCode(),
                normalizedSystemId.Value,
                normalizedFriendSystemId.Value));
            batch.Add(new SimpleStatement(
                "UPDATE global.friendships_by_friend_id SET level = ? WHERE friend_id = ? AND user_id = ?",
                (trusted ? FriendshipLevel.TrustedFriend : FriendshipLevel.Friend).ToCode(),
                normalizedFriendSystemId.Value,
                normalizedSystemId.Value));
            await session.ExecuteAsync(batch);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeTyped(systemId);
            var profileHydrationConcurrency = _options.HydrationMaxConcurrency;

            var incomingTask = session.ExecuteAsync(new SimpleStatement(
                "SELECT from_id, date_sent FROM global.friend_requests_by_to_id WHERE to_id = ?",
                normalizedSystemId.Value));

            var outgoingTask = session.ExecuteAsync(new SimpleStatement(
                "SELECT to_id, date_sent FROM global.friend_requests WHERE from_id = ?",
                normalizedSystemId.Value));

            await Task.WhenAll(incomingTask, outgoingTask);

            var incomingRows = await incomingTask;
            var outgoingRows = await outgoingTask;

            var incoming = await ConcurrentProjection.SelectWithConcurrencyAsync(
                incomingRows,
                profileHydrationConcurrency,
                async row =>
                {
                    var sourceSystemId = new SystemId(row.GetValue<string>("from_id"));
                    var profile = await GetFriendProfileAsync(session, sourceSystemId);
                    return new FriendRequestReadModel(profile, new FriendshipRequestModel(row.GetValue<DateTimeOffset?>("date_sent") ?? DateTimeOffset.UtcNow));
                },
                cancellationToken);

            var outgoing = await ConcurrentProjection.SelectWithConcurrencyAsync(
                outgoingRows,
                profileHydrationConcurrency,
                async row =>
                {
                    var targetSystemId = new SystemId(row.GetValue<string>("to_id"));
                    var profile = await GetFriendProfileAsync(session, targetSystemId);
                    return new FriendRequestReadModel(profile, new FriendshipRequestModel(row.GetValue<DateTimeOffset?>("date_sent") ?? DateTimeOffset.UtcNow));
                },
                cancellationToken);

            return new FriendRequestIndexReadModel(
                incoming.OrderByDescending(x => x.Request.DateSent).ToList(),
                outgoing.OrderByDescending(x => x.Request.DateSent).ToList());
        }, _options, cancellationToken);
    }

    public async Task<SendFriendRequestOutcome> SendRequestAsync(SystemId systemId, SystemId targetSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeTyped(systemId);
            var normalizedTargetSystemId = _keyspaceResolver.NormalizeTyped(targetSystemId);

            var resolvedTargetUserId = await ResolveUserIdInScyllaAsync(session, normalizedTargetSystemId);
            if (resolvedTargetUserId is null)
            {
                return SendFriendRequestOutcome.NoUser;
            }
            normalizedTargetSystemId = resolvedTargetUserId.Value;

            if (await ExistsFriendshipAsync(session, normalizedSystemId, normalizedTargetSystemId))
            {
                return SendFriendRequestOutcome.AlreadyFriends;
            }

            if (await ExistsRequestAsync(session, normalizedSystemId, normalizedTargetSystemId))
            {
                return SendFriendRequestOutcome.AlreadySent;
            }

            if (await ExistsRequestAsync(session, normalizedTargetSystemId, normalizedSystemId))
            {
                await LinkFriendsAndClearRequestsAsync(session, normalizedSystemId, normalizedTargetSystemId);
                return SendFriendRequestOutcome.Accepted;
            }

            await CreateRequestAsync(session, normalizedSystemId, normalizedTargetSystemId);
            return SendFriendRequestOutcome.Sent;
        }, _options, cancellationToken);
    }

    public async Task<FriendRequestMutationOutcome> AcceptRequestAsync(SystemId systemId, SystemId sourceSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeTyped(systemId);
            var normalizedSourceSystemId = _keyspaceResolver.NormalizeTyped(sourceSystemId);

            var resolvedSourceUserId = await ResolveUserIdInScyllaAsync(session, normalizedSourceSystemId);
            if (resolvedSourceUserId is null)
            {
                return FriendRequestMutationOutcome.NoUser;
            }
            normalizedSourceSystemId = resolvedSourceUserId.Value;

            if (await ExistsFriendshipAsync(session, normalizedSystemId, normalizedSourceSystemId))
            {
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await ExistsRequestAsync(session, normalizedSourceSystemId, normalizedSystemId))
            {
                return FriendRequestMutationOutcome.NotRequested;
            }

            await LinkFriendsAndClearRequestsAsync(session, normalizedSystemId, normalizedSourceSystemId);
            return FriendRequestMutationOutcome.Ok;
        }, _options, cancellationToken);
    }

    public async Task<FriendRequestMutationOutcome> RejectRequestAsync(SystemId systemId, SystemId sourceSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeTyped(systemId);
            var normalizedSourceSystemId = _keyspaceResolver.NormalizeTyped(sourceSystemId);

            var resolvedSourceUserId = await ResolveUserIdInScyllaAsync(session, normalizedSourceSystemId);
            if (resolvedSourceUserId is null)
            {
                return FriendRequestMutationOutcome.NoUser;
            }
            normalizedSourceSystemId = resolvedSourceUserId.Value;

            if (await ExistsFriendshipAsync(session, normalizedSystemId, normalizedSourceSystemId))
            {
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await ExistsRequestAsync(session, normalizedSourceSystemId, normalizedSystemId))
            {
                return FriendRequestMutationOutcome.NotRequested;
            }

            await DeleteRequestAsync(session, normalizedSourceSystemId, normalizedSystemId);
            return FriendRequestMutationOutcome.Ok;
        }, _options, cancellationToken);
    }

    public async Task<FriendRequestMutationOutcome> CancelRequestAsync(SystemId systemId, SystemId targetSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeTyped(systemId);
            var normalizedTargetSystemId = _keyspaceResolver.NormalizeTyped(targetSystemId);

            var resolvedTargetUserId = await ResolveUserIdInScyllaAsync(session, normalizedTargetSystemId);
            if (resolvedTargetUserId is null)
            {
                return FriendRequestMutationOutcome.NoUser;
            }
            normalizedTargetSystemId = resolvedTargetUserId.Value;

            if (await ExistsFriendshipAsync(session, normalizedSystemId, normalizedTargetSystemId))
            {
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await ExistsRequestAsync(session, normalizedSystemId, normalizedTargetSystemId))
            {
                return FriendRequestMutationOutcome.NotRequested;
            }

            await DeleteRequestAsync(session, normalizedSystemId, normalizedTargetSystemId);
            return FriendRequestMutationOutcome.Ok;
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<SystemId>> DeleteAllForSystemAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeTyped(systemId);

            // Find all friendships for this user
            var friendsTask = session.ExecuteAsync(new SimpleStatement(
                "SELECT friend_id FROM global.friendships WHERE user_id = ?",
                normalizedSystemId.Value));

            // Find all outgoing requests
            var outgoingRequestsTask = session.ExecuteAsync(new SimpleStatement(
                "SELECT to_id FROM global.friend_requests WHERE from_id = ?",
                normalizedSystemId.Value));

            // Find all incoming requests
            var incomingRequestsTask = session.ExecuteAsync(new SimpleStatement(
                "SELECT from_id FROM global.friend_requests_by_to_id WHERE to_id = ?",
                normalizedSystemId.Value));

            await Task.WhenAll(friendsTask, outgoingRequestsTask, incomingRequestsTask);

            var friendRows = await friendsTask;
            var outgoingRows = await outgoingRequestsTask;
            var incomingRows = await incomingRequestsTask;

            var batch = new BatchStatement();
            var friendIds = new List<SystemId>();
            foreach (var row in friendRows)
            {
                var friendId = new SystemId(row.GetValue<string>("friend_id"));
                friendIds.Add(friendId);
                batch.Add(new SimpleStatement("DELETE FROM global.friendships WHERE user_id = ? AND friend_id = ?", normalizedSystemId.Value, friendId.Value));
                batch.Add(new SimpleStatement("DELETE FROM global.friendships WHERE user_id = ? AND friend_id = ?", friendId.Value, normalizedSystemId.Value));
                batch.Add(new SimpleStatement("DELETE FROM global.friendships_by_friend_id WHERE friend_id = ? AND user_id = ?", friendId.Value, normalizedSystemId.Value));
                batch.Add(new SimpleStatement("DELETE FROM global.friendships_by_friend_id WHERE friend_id = ? AND user_id = ?", normalizedSystemId.Value, friendId.Value));
            }

            foreach (var row in outgoingRows)
            {
                var targetId = row.GetValue<string>("to_id");
                batch.Add(new SimpleStatement("DELETE FROM global.friend_requests WHERE from_id = ? AND to_id = ?", normalizedSystemId.Value, targetId));
                batch.Add(new SimpleStatement("DELETE FROM global.friend_requests_by_to_id WHERE to_id = ? AND from_id = ?", targetId, normalizedSystemId.Value));
            }

            foreach (var row in incomingRows)
            {
                var sourceId = row.GetValue<string>("from_id");
                batch.Add(new SimpleStatement("DELETE FROM global.friend_requests WHERE from_id = ? AND to_id = ?", sourceId, normalizedSystemId.Value));
                batch.Add(new SimpleStatement("DELETE FROM global.friend_requests_by_to_id WHERE to_id = ? AND from_id = ?", normalizedSystemId.Value, sourceId));
            }

            if (!batch.IsEmpty)
            {
                await session.ExecuteAsync(batch);
            }

            return (IReadOnlyList<SystemId>)friendIds.ToArray();
        }, _options, cancellationToken);
    }


    private static async Task<bool> ExistsFriendshipAsync(ISession session, SystemId userId, SystemId friendId)
    {
        var query = new SimpleStatement(
            "SELECT friend_id FROM global.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
            userId.Value,
            friendId.Value);

        return (await session.ExecuteAsync(query)).Any();
    }

    private static async Task<bool> SystemExistsAsync(ISession session, SystemId userId)
    {
        var query = new SimpleStatement(
            "SELECT user_id FROM global.user_registry WHERE user_id = ? LIMIT 1",
            userId.Value);

        return (await session.ExecuteAsync(query)).Any();
    }

    private static async Task<SystemId?> ResolveUserIdInScyllaAsync(ISession session, SystemId userNameOrId)
    {
        if (string.IsNullOrWhiteSpace(userNameOrId.Value))
        {
            return null;
        }

        var input = userNameOrId.Value.Trim();

        // First, try direct lookup in user_registry (handles 7-char IDs)
        var directQuery = new SimpleStatement(
            "SELECT user_id FROM global.user_registry WHERE user_id = ? LIMIT 1",
            input);
        var directRow = (await session.ExecuteAsync(directQuery)).FirstOrDefault();
        if (directRow != null)
        {
            return new SystemId(directRow.GetValue<string>("user_id"));
        }

        var existingKeyspaces = await GetExistingRegionalKeyspacesAsync(session);

        // If not found as direct ID, try as username via denormalized lookup table across known regional keyspaces.
        foreach (var region in existingKeyspaces.Where(CanonicalRegions.Contains))
        {
            try
            {
                var userQuery = new SimpleStatement(
                    $"SELECT user_id FROM {region}.users_by_username WHERE username = ? LIMIT 1",
                    input);
                var userRow = (await session.ExecuteAsync(userQuery)).FirstOrDefault();
                if (userRow != null)
                {
                    return new SystemId(userRow.GetValue<string>("user_id"));
                }
            }
            catch (UnavailableException)
            {
                // Region keyspace/table temporarily unavailable; skip and try next region
                continue;
            }
            catch (InvalidQueryException)
            {
                // Table doesn't exist in this keyspace; skip and try next region
                continue;
            }
        }

        return null;
    }

    private static async Task<HashSet<string>> GetExistingRegionalKeyspacesAsync(ISession session)
    {
        var keyspacesQuery = new SimpleStatement("SELECT keyspace_name FROM system_schema.keyspaces");
        var keyspaceRows = await session.ExecuteAsync(keyspacesQuery);

        return keyspaceRows
            .Select(row => row.GetValue<string>("keyspace_name").ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> ExistsRequestAsync(ISession session, SystemId fromId, SystemId toId)
    {
        var query = new SimpleStatement(
            "SELECT to_id FROM global.friend_requests WHERE from_id = ? AND to_id = ? LIMIT 1",
            fromId.Value,
            toId.Value);

        return (await session.ExecuteAsync(query)).Any();
    }

    private static async Task CreateRequestAsync(ISession session, SystemId fromId, SystemId toId)
    {
        var batch = new BatchStatement();
        batch.Add(new SimpleStatement(
            "INSERT INTO global.friend_requests (from_id, to_id, date_sent, inserted_at, updated_at) VALUES (?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            fromId.Value, toId.Value));
        batch.Add(new SimpleStatement(
            "INSERT INTO global.friend_requests_by_to_id (to_id, from_id, date_sent, inserted_at, updated_at) VALUES (?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            toId.Value, fromId.Value));
        await session.ExecuteAsync(batch);
    }

    private static async Task DeleteRequestAsync(ISession session, SystemId fromId, SystemId toId)
    {
        var batch = new BatchStatement();
        batch.Add(new SimpleStatement(
            "DELETE FROM global.friend_requests WHERE from_id = ? AND to_id = ?",
            fromId.Value, toId.Value));
        batch.Add(new SimpleStatement(
            "DELETE FROM global.friend_requests_by_to_id WHERE to_id = ? AND from_id = ?",
            toId.Value, fromId.Value));
        await session.ExecuteAsync(batch);
    }

    private static async Task LinkFriendsAndClearRequestsAsync(ISession session, SystemId systemId, SystemId otherSystemId)
    {
        var friendLevel = FriendshipLevel.Friend.ToCode();
        var batch = new BatchStatement();
        batch.Add(new SimpleStatement(
            "INSERT INTO global.friendships (user_id, friend_id, level, since, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            systemId.Value, otherSystemId.Value, friendLevel));
        batch.Add(new SimpleStatement(
            "INSERT INTO global.friendships (user_id, friend_id, level, since, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            otherSystemId.Value, systemId.Value, friendLevel));
        // Maintain friendships_by_friend_id denormalized table
        batch.Add(new SimpleStatement(
            "INSERT INTO global.friendships_by_friend_id (friend_id, user_id, level, since, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            otherSystemId.Value, systemId.Value, friendLevel));
        batch.Add(new SimpleStatement(
            "INSERT INTO global.friendships_by_friend_id (friend_id, user_id, level, since, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            systemId.Value, otherSystemId.Value, friendLevel));
        // Clear requests in both directions
        batch.Add(new SimpleStatement(
            "DELETE FROM global.friend_requests WHERE from_id = ? AND to_id = ?",
            systemId.Value, otherSystemId.Value));
        batch.Add(new SimpleStatement(
            "DELETE FROM global.friend_requests WHERE from_id = ? AND to_id = ?",
            otherSystemId.Value, systemId.Value));
        batch.Add(new SimpleStatement(
            "DELETE FROM global.friend_requests_by_to_id WHERE to_id = ? AND from_id = ?",
            otherSystemId.Value, systemId.Value));
        batch.Add(new SimpleStatement(
            "DELETE FROM global.friend_requests_by_to_id WHERE to_id = ? AND from_id = ?",
            systemId.Value, otherSystemId.Value));
        await session.ExecuteAsync(batch);
    }

    private async Task<FriendProfileReadModel> GetFriendProfileAsync(ISession session, SystemId friendSystemId)
    {
        var resolvedRegion = await ResolveUserRegionAsync(session, friendSystemId);
        if (resolvedRegion is not { } typedRegion)
        {
            return new FriendProfileReadModel(friendSystemId, null, null, null, null, null);
        }

        var region = typedRegion.ToWireValue();
        var profileQuery = new SimpleStatement(
            $"SELECT username, avatar_url, avatar_source, description, discord_id FROM {region}.users WHERE id = ? LIMIT 1",
            friendSystemId.Value);

        var profileRow = (await session.ExecuteAsync(profileQuery)).FirstOrDefault();

        return new FriendProfileReadModel(
            friendSystemId,
            profileRow?.GetValue<string?>("username") is { } username ? new Username(username) : null,
            AvatarUrl.FromNullable(profileRow?.GetValue<string?>("avatar_url")),
            AvatarSourceExtensions.TryFromCode(profileRow?.GetValue<short?>("avatar_source")),
            profileRow?.GetValue<string?>("description"),
            profileRow?.GetValue<string?>("discord_id") is { } discordId ? new DiscordId(discordId) : null);
    }

    private async Task<IReadOnlyList<FriendFrontingReadModel>> GetFrontingAsync(ISession session, SystemId friendSystemId, SystemId viewerSystemId)
    {
        var resolvedRegion = await ResolveUserRegionAsync(session, friendSystemId);
        if (resolvedRegion is not { } typedRegion)
        {
            return [];
        }

        var regionalKeyspace = typedRegion.ToWireValue();

        // Friendship level from the friend's perspective (they control their own alter visibility)
        var levelTask = session.ExecuteAsync(new SimpleStatement(
            "SELECT level FROM global.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
            friendSystemId.Value,
            viewerSystemId.Value));
        var activeTask = session.ExecuteAsync(new SimpleStatement(
            $"SELECT alter_id, comment FROM {regionalKeyspace}.current_fronts WHERE user_id = ?",
            friendSystemId.Value));
        var primaryTask = session.ExecuteAsync(new SimpleStatement(
            $"SELECT primary_front FROM {regionalKeyspace}.users WHERE id = ? LIMIT 1",
            friendSystemId.Value));
        var altersTask = session.ExecuteAsync(new SimpleStatement(
            $"SELECT id, name, avatar_url, avatar_source, pronouns, color, description, extra_images, security_level FROM {regionalKeyspace}.alters WHERE user_id = ?",
            friendSystemId.Value));

        await Task.WhenAll(levelTask, activeTask, primaryTask, altersTask);

        var levelRow = (await levelTask).FirstOrDefault();
        FriendshipLevel? friendshipLevel = levelRow is null ? null : FriendshipLevelExtensions.FromCode(levelRow.GetValue<short>("level"));
        var activeRows = await activeTask;
        var primaryRow = (await primaryTask).FirstOrDefault();
        var primaryAlterId = AlterId.FromStorageInt(primaryRow?.GetValue<int?>("primary_front"));
        var alterRows = await altersTask;

        var alterMap = alterRows
            .Where(row => CanViewAlter(friendshipLevel, row.GetValue<short?>("security_level")))
            .ToDictionary(
                row => row.GetValue<short>("id"),
                row => (
                    Name: row.GetValue<string?>("name"),
                    AvatarUrl: AvatarUrl.FromNullable(row.GetValue<string?>("avatar_url")),
                    AvatarSource: AvatarSourceExtensions.TryFromCode(row.GetValue<short?>("avatar_source")),
                    Pronouns: row.GetValue<string?>("pronouns"),
                    Color: HexColor.FromNullable(row.GetValue<string?>("color")),
                    Description: row.GetValue<string?>("description"),
                    ExtraImages: (IReadOnlyList<AvatarUrl>)(row.GetValue<IEnumerable<string>?>("extra_images")?.Select(url => new AvatarUrl(url)).ToList() ?? [])));

        return activeRows
            .Select(row =>
            {
                var alterIdShort = row.GetValue<short>("alter_id");
                var alterId = AlterId.FromStorageShort(alterIdShort);
                if (!alterMap.TryGetValue(alterIdShort, out var alter))
                {
                    return null;
                }
                return new FriendFrontingReadModel(
                    new FriendFrontingAlterReadModel(
                        alterId,
                        alter.Name,
                        alter.Pronouns,
                        alter.Description,
                        [],
                        alter.AvatarUrl,
                        alter.AvatarSource,
                        alter.ExtraImages,
                        alter.Color),
                    new FriendFrontingFrontReadModel(alterId, row.GetValue<string?>("comment")),
                    primaryAlterId == alterId);
            })
            .Where(x => x is not null)
            .OrderBy(x => x!.Alter.Id.Value)
            .ToList()!;
    }

    private static bool CanViewAlter(FriendshipLevel? friendshipLevel, short? securityLevel)
        => VisibilityLevelExtensions.FromCodeOrPublic(securityLevel).CanBeViewedBy(friendshipLevel);

    // Returns null when the registry row is absent or carries an unknown region value —
    // callers treat both as "profile unavailable" rather than guessing a keyspace.
    private static async Task<ScyllaKeyspace?> ResolveUserRegionAsync(ISession session, SystemId userId)
    {
        var regionQuery = new SimpleStatement(
            "SELECT region FROM global.user_registry WHERE user_id = ? LIMIT 1",
            userId.Value);

        var row = (await session.ExecuteAsync(regionQuery)).FirstOrDefault();
        var raw = row?.GetValue<string>("region");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return EnumWireExtensions.ParseScyllaKeyspace(raw);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
