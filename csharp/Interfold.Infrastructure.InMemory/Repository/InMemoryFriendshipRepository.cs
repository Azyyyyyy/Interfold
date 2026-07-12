using System.Collections.Concurrent;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryFriendshipRepository : IFriendshipRepository
{
    private sealed class FriendshipState
    {
        public required SystemId FriendSystemId { get; init; }
        public required FriendshipLevel Level { get; set; }
        public required DateTimeOffset Since { get; init; }
    }

    private sealed class RequestState
    {
        public required SystemId OtherSystemId { get; init; }
        public required DateTimeOffset DateSent { get; init; }
    }

    private readonly ConcurrentDictionary<SystemId, ConcurrentDictionary<SystemId, FriendshipState>> _friendships = new();
    private readonly ConcurrentDictionary<SystemId, ConcurrentDictionary<SystemId, RequestState>> _outgoingRequests = new();
    private readonly IAccountRepository? _accounts;

    /// <summary>
    /// Parameterless / find-only-account ctor used from the DI container.
    /// <paramref name="accounts"/> is optional so the older tests that instantiate this
    /// repository directly (e.g. <c>InMemoryNotificationTokenRepositoryTests</c>) don't
    /// have to spin up an account repository; those tests never exercise the
    /// <c>Kind.Discord</c> branch, so a null delegate is safe there. Production wiring
    /// always passes a real <see cref="IAccountRepository"/>.
    /// </summary>
    public InMemoryFriendshipRepository(IAccountRepository? accounts = null)
    {
        _accounts = accounts;
    }

    public async Task<SystemId?> ResolveUserIdAsync(UsernameOrSystemId userNameOrId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userNameOrId))
        {
            return null;
        }

        var input = userNameOrId.Value.Trim();

        // Mirror the ScyllaFriendshipRepository routing table so both backends dispatch
        // identically. InMemory has no user_registry / users_by_username tables, so
        // Kind.Region / Kind.Id inputs collapse onto "normalise and return" — tests
        // explicitly seed users via EnsureUserExistsAsync so the returned SystemId
        // always maps to a real record. Kind.Username has no reverse-index — returning
        // null matches "no such username" cleanly.
        //
        // Unparseable inputs (unknown non-region prefix like "xxx:foo", or the
        // colon-at-boundary shapes ":foo" / "foo:") return null. On Scylla the same
        // input round-trips into a user_registry.user_id miss; InMemory has no such
        // intermediate lookup so round-tripping would flow the opaque id straight into
        // the friend-request writer with no existence check, creating a phantom request.
        // Short-circuiting here keeps the observable outcome (422 friend_request:no_user)
        // aligned across all backends.
        if (!LookupHandle.TryParse(input, out var handle))
        {
            return null;
        }

        return handle.Kind switch
        {
            LookupKind.Discord => _accounts is null
                ? null
                : await _accounts.TryFindSystemIdByDiscordIdAsync(new(handle.RawId), cancellationToken),
            LookupKind.Username => null,
            // Region / Id both target the raw system id; Normalize collapses "nam:abc"
            // → "abc" so the storage key matches what other InMemory repos wrote when
            // the caller passed the scoped shape.
            _ => Normalize(new(handle.RawId)),
        };
    }

    public Task<FriendshipLevel?> GetFriendshipLevelAsync(SystemId systemId, SystemId? viewerSystemId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(viewerSystemId?.Value))
        {
            return Task.FromResult<FriendshipLevel?>(null);
        }

        var normalizedSystemId = Normalize(systemId);
        var normalizedViewerId = Normalize(viewerSystemId.Value);

        if (normalizedSystemId == normalizedViewerId)
        {
            return Task.FromResult<FriendshipLevel?>(FriendshipLevel.TrustedFriend);
        }

        if (!_friendships.TryGetValue(normalizedSystemId, out var store) || !store.TryGetValue(normalizedViewerId, out var state))
        {
            return Task.FromResult<FriendshipLevel?>(null);
        }

        return Task.FromResult<FriendshipLevel?>(state.Level);
    }

    public Task<IReadOnlyList<FriendshipReadModel>> ListFriendshipsAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = Normalize(systemId);
        if (!_friendships.TryGetValue(normalizedSystemId, out var store))
        {
            return Task.FromResult<IReadOnlyList<FriendshipReadModel>>(Array.Empty<FriendshipReadModel>());
        }

        var list = store.Values
            .OrderByDescending(x => x.Since)
            .Select(x => new FriendshipReadModel(
                new FriendProfileReadModel(x.FriendSystemId, null, null, null, null, null),
                new FriendshipModel(
                x.Level,
                x.Since),
                Array.Empty<FriendFrontingReadModel>()))
            .ToList();

        return Task.FromResult<IReadOnlyList<FriendshipReadModel>>(list);
    }

    public Task<FriendshipReadModel?> GetFriendshipAsync(SystemId systemId, SystemId friendSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = Normalize(systemId);
        var normalizedFriendId = Normalize(friendSystemId);

        if (!_friendships.TryGetValue(normalizedSystemId, out var store) || !store.TryGetValue(normalizedFriendId, out var state))
        {
            return Task.FromResult<FriendshipReadModel?>(null);
        }

        return Task.FromResult<FriendshipReadModel?>(new FriendshipReadModel(
            new FriendProfileReadModel(state.FriendSystemId, null, null, null, null, null),
            new FriendshipModel(
                state.Level,
                state.Since),
            Array.Empty<FriendFrontingReadModel>()));
    }

    public Task<bool> RemoveFriendshipAsync(SystemId systemId, SystemId friendSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = Normalize(systemId);
        var normalizedFriendId = Normalize(friendSystemId);

        if (!_friendships.TryGetValue(normalizedSystemId, out var userStore) || !userStore.TryRemove(normalizedFriendId, out _))
        {
            return Task.FromResult(false);
        }

        if (_friendships.TryGetValue(normalizedFriendId, out var peerStore))
        {
            peerStore.TryRemove(normalizedSystemId, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> SetTrustedAsync(SystemId systemId, SystemId friendSystemId, bool trusted, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = Normalize(systemId);
        var normalizedFriendId = Normalize(friendSystemId);

        if (!_friendships.TryGetValue(normalizedSystemId, out var store) || !store.TryGetValue(normalizedFriendId, out var state))
        {
            return Task.FromResult(false);
        }

        state.Level = trusted ? FriendshipLevel.TrustedFriend : FriendshipLevel.Friend;
        return Task.FromResult(true);
    }

    public Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = Normalize(systemId);

        var outgoing = _outgoingRequests.TryGetValue(normalizedSystemId, out var outStore)
            ? outStore.Values
                .OrderByDescending(r => r.DateSent)
                .Select(r => new FriendRequestReadModel(
                    new FriendProfileReadModel(r.OtherSystemId, null, null, null, null, null),
                    new FriendshipRequestModel(r.DateSent)))
                .ToList()
            : new List<FriendRequestReadModel>();

        var incoming = _outgoingRequests
            .SelectMany(kvp => kvp.Value.Values.Select(r => (From: kvp.Key, Request: r)))
            .Where(x => x.Request.OtherSystemId == normalizedSystemId)
            .OrderByDescending(x => x.Request.DateSent)
            .Select(x => new FriendRequestReadModel(
                new FriendProfileReadModel(x.From, null, null, null, null, null),
                new FriendshipRequestModel(x.Request.DateSent)))
            .ToList();

        return Task.FromResult(new FriendRequestIndexReadModel(incoming, outgoing));
    }

    public Task<SendFriendRequestOutcome> SendRequestAsync(SystemId systemId, SystemId targetSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = Normalize(systemId);
        var normalizedTargetId = Normalize(targetSystemId);

        if (IsFriends(normalizedSystemId, normalizedTargetId))
        {
            return Task.FromResult(SendFriendRequestOutcome.AlreadyFriends);
        }

        if (HasOutgoingRequest(normalizedSystemId, normalizedTargetId))
        {
            return Task.FromResult(SendFriendRequestOutcome.AlreadySent);
        }

        if (HasOutgoingRequest(normalizedTargetId, normalizedSystemId))
        {
            LinkFriends(normalizedSystemId, normalizedTargetId);
            RemoveRequest(normalizedTargetId, normalizedSystemId);
            RemoveRequest(normalizedSystemId, normalizedTargetId);
            return Task.FromResult(SendFriendRequestOutcome.Accepted);
        }

        var store = _outgoingRequests.GetOrAdd(normalizedSystemId, _ => new ConcurrentDictionary<SystemId, RequestState>());
        store[normalizedTargetId] = new RequestState
        {
            OtherSystemId = normalizedTargetId,
            DateSent = DateTimeOffset.UtcNow
        };

        return Task.FromResult(SendFriendRequestOutcome.Sent);
    }

    public Task<FriendRequestMutationOutcome> AcceptRequestAsync(SystemId systemId, SystemId sourceSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = Normalize(systemId);
        var normalizedSourceId = Normalize(sourceSystemId);

        if (IsFriends(normalizedSystemId, normalizedSourceId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.AlreadyFriends);
        }

        if (!HasOutgoingRequest(normalizedSourceId, normalizedSystemId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.NotRequested);
        }

        LinkFriends(normalizedSystemId, normalizedSourceId);
        RemoveRequest(normalizedSourceId, normalizedSystemId);
        RemoveRequest(normalizedSystemId, normalizedSourceId);
        return Task.FromResult(FriendRequestMutationOutcome.Ok);
    }

    public Task<FriendRequestMutationOutcome> RejectRequestAsync(SystemId systemId, SystemId sourceSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = Normalize(systemId);
        var normalizedSourceId = Normalize(sourceSystemId);

        if (IsFriends(normalizedSystemId, normalizedSourceId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.AlreadyFriends);
        }

        if (!HasOutgoingRequest(normalizedSourceId, normalizedSystemId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.NotRequested);
        }

        RemoveRequest(normalizedSourceId, normalizedSystemId);
        return Task.FromResult(FriendRequestMutationOutcome.Ok);
    }

    public Task<FriendRequestMutationOutcome> CancelRequestAsync(SystemId systemId, SystemId targetSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = Normalize(systemId);
        var normalizedTargetId = Normalize(targetSystemId);

        if (IsFriends(normalizedSystemId, normalizedTargetId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.AlreadyFriends);
        }

        if (!HasOutgoingRequest(normalizedSystemId, normalizedTargetId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.NotRequested);
        }

        RemoveRequest(normalizedSystemId, normalizedTargetId);
        return Task.FromResult(FriendRequestMutationOutcome.Ok);
    }

    public Task<IReadOnlyList<SystemId>> DeleteAllForSystemAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = Normalize(systemId);
        var friendIds = new List<SystemId>();

        // Remove all friendships where this user is involved
        if (_friendships.TryRemove(normalizedSystemId, out var friends))
        {
            foreach (var friendId in friends.Keys)
            {
                friendIds.Add(friendId);
                if (_friendships.TryGetValue(friendId, out var peerStore))
                {
                    peerStore.TryRemove(normalizedSystemId, out _);
                }
            }
        }

        // Remove all outgoing requests from this user
        _outgoingRequests.TryRemove(normalizedSystemId, out _);

        // Remove all incoming requests to this user (we need to scan for this in-memory)
        foreach (var requesterId in _outgoingRequests.Keys)
        {
            if (_outgoingRequests.TryGetValue(requesterId, out var requests))
            {
                requests.TryRemove(normalizedSystemId, out _);
            }
        }

        return Task.FromResult<IReadOnlyList<SystemId>>(friendIds.ToArray());
    }

    private bool IsFriends(SystemId systemId, SystemId friendSystemId)
        => _friendships.TryGetValue(systemId, out var store) && store.ContainsKey(friendSystemId);

    private bool HasOutgoingRequest(SystemId fromSystemId, SystemId toSystemId)
        => _outgoingRequests.TryGetValue(fromSystemId, out var store) && store.ContainsKey(toSystemId);

    private void RemoveRequest(SystemId fromSystemId, SystemId toSystemId)
    {
        if (_outgoingRequests.TryGetValue(fromSystemId, out var store))
        {
            store.TryRemove(toSystemId, out _);
        }
    }

    private void LinkFriends(SystemId left, SystemId right)
    {
        var now = DateTimeOffset.UtcNow;

        var leftStore = _friendships.GetOrAdd(left, _ => new ConcurrentDictionary<SystemId, FriendshipState>());
        leftStore[right] = new FriendshipState
        {
            FriendSystemId = right,
            Level = FriendshipLevel.Friend,
            Since = now
        };

        var rightStore = _friendships.GetOrAdd(right, _ => new ConcurrentDictionary<SystemId, FriendshipState>());
        rightStore[left] = new FriendshipState
        {
            FriendSystemId = left,
            Level = FriendshipLevel.Friend,
            Since = now
        };
    }

    private static SystemId Normalize(SystemId systemId) => InMemoryStorageKeys.Normalize(systemId);
}
