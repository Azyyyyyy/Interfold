using System.Net;
using Interfold.Friendships.Api.Helpers;
using Interfold.Friendships.Contracts;
using Interfold.Friendships.Contracts.Ids;
using Interfold.Friendships.Contracts.Models.Commands;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Friendships.Domain;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Shared.Api.Http;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Operations;

namespace Interfold.Friendships.Api.DependencyInjection;

public static partial class FriendshipsModuleServiceCollectionExtensions
{
    public static IEndpointRouteBuilder MapFriendshipsModule(this IEndpointRouteBuilder endpoints)
    {
        var friends = endpoints.MapInterfoldGroup("/api/friends");
        friends.MapGet("/", ListFriends);
        friends.MapGet("/{id}", ShowFriend);
        friends.MapDelete("/{id}", DeleteFriend);
        friends.MapPost("/{id}/trust", Trust);
        friends.MapPost("/{id}/untrust", Untrust);

        var requests = endpoints.MapInterfoldGroup("/api/friend-requests");
        requests.MapGet("/", ListRequests);
        requests.MapPut("/{id}", Send);
        requests.MapDelete("/{id}", Cancel);
        requests.MapPost("/{id}/accept", Accept);
        requests.MapPost("/{id}/reject", Reject);
        return endpoints;
    }

    private static async Task<Response<IReadOnlyList<FriendshipReadModel>>> ListFriends(
        HttpContext http, IFriendshipRepository repository, CancellationToken ct)
    {
        var friendships = await repository.ListFriendshipsAsync(http.PrincipalId(), ct);
        var qualified = friendships
            .Select(f => FriendshipAvatarQualifier.QualifyFriendship(f, http.Request.Scheme, http.Request.Host))
            .ToArray();
        return new SuccessResponse<IReadOnlyList<FriendshipReadModel>>(qualified);
    }

    private static async Task<Response<FriendshipReadModel>> ShowFriend(
        HttpContext http, SystemId id, IFriendshipRepository repository, CancellationToken ct)
    {
        if (http.RejectIfSelf(
                id,
                "I'm pretty sure you don't count as your own friend. (Cannot view friendship status for self.)",
                ErrorCodes.CannotViewOwnFriendship) is { } reject)
            return reject;

        var friendship = await repository.GetFriendshipAsync(http.PrincipalId(), id, ct);
        return InterfoldHttp.OkOrNotFound(
            friendship is null ? null : FriendshipAvatarQualifier.QualifyFriendship(friendship, http.Request.Scheme, http.Request.Host),
            "You are not friends with that system.",
            ErrorCodes.FriendshipNotFound);
    }

    private static Task<Response> DeleteFriend(
        HttpContext http, SystemId id, RemoveFriendshipCommandHandler remove, CancellationToken ct)
    {
        if (http.RejectIfSelf(
                id,
                "I'm pretty sure you don't count as your own friend. (Cannot delete friendship with self.)",
                ErrorCodes.CannotDeleteOwnFriendship) is { } reject)
            return Task.FromResult<Response>(reject);

        return http.DispatchNoContentAsync(remove, OperationIds.FriendDelete, new RemoveFriendshipCommand(id), ct);
    }

    private static Task<Response> Trust(
        HttpContext http, SystemId id, SetFriendTrustCommandHandler setTrust, CancellationToken ct)
        => SetTrustInternal(http, id, true, OperationIds.FriendTrust, ErrorCodes.CannotTrustSelf, setTrust, ct);

    private static Task<Response> Untrust(
        HttpContext http, SystemId id, SetFriendTrustCommandHandler setTrust, CancellationToken ct)
        => SetTrustInternal(http, id, false, OperationIds.FriendUntrust, ErrorCodes.CannotUntrustSelf, setTrust, ct);

    private static Task<Response> SetTrustInternal(
        HttpContext http,
        SystemId id,
        bool trusted,
        OperationId operationId,
        ErrorCode selfErrorCode,
        SetFriendTrustCommandHandler setTrust,
        CancellationToken ct)
    {
        if (http.PrincipalId().RepresentsSameUserAs(id))
            return Task.FromResult<Response>(new ErrorResponse("Cannot trust self.", selfErrorCode, HttpStatusCode.BadRequest));

        return http.DispatchNoContentAsync(setTrust, operationId, new SetFriendTrustCommand(id, trusted), ct);
    }

    private static async Task<Response<FriendRequestIndexReadModel>> ListRequests(
        HttpContext http, IFriendshipRepository repository, CancellationToken ct)
    {
        var requests = await repository.GetFriendRequestsAsync(http.PrincipalId(), ct);
        var incoming = requests.Incoming
            .Select(r => FriendshipAvatarQualifier.QualifyFriendRequest(r, http.Request.Scheme, http.Request.Host))
            .ToArray();
        var outgoing = requests.Outgoing
            .Select(r => FriendshipAvatarQualifier.QualifyFriendRequest(r, http.Request.Scheme, http.Request.Host))
            .ToArray();

        return new SuccessResponse<FriendRequestIndexReadModel>(new FriendRequestIndexReadModel(incoming, outgoing));
    }

    private static Task<Response> Send(
        HttpContext http, FriendLookup id, SendFriendRequestCommandHandler send, CancellationToken ct)
    {
        if (id.RepresentsSameUserAs(http.PrincipalId()))
            return Task.FromResult<Response>(new ErrorResponse("You cannot send a friend request to yourself.", ErrorCodes.CannotSendSelf, HttpStatusCode.BadRequest));

        return http.DispatchNoContentAsync(send, OperationIds.FriendRequestSend, new SendFriendRequestCommand(id), ct);
    }

    private static Task<Response> Cancel(
        HttpContext http, SystemId id, CancelFriendRequestCommandHandler cancel, CancellationToken ct)
    {
        if (http.RejectIfSelf(id, "You cannot cancel a friend request to yourself.", ErrorCodes.CannotCancelSelf) is { } reject)
            return Task.FromResult<Response>(reject);

        return http.DispatchNoContentAsync(cancel, OperationIds.FriendRequestCancel, new CancelFriendRequestCommand(id), ct);
    }

    private static Task<Response> Accept(
        HttpContext http, SystemId id, AcceptFriendRequestCommandHandler accept, CancellationToken ct)
    {
        if (http.RejectIfSelf(id, "You cannot accept a friend request from yourself.", ErrorCodes.CannotAcceptSelf) is { } reject)
            return Task.FromResult<Response>(reject);

        return http.DispatchNoContentAsync(accept, OperationIds.FriendRequestAccept, new AcceptFriendRequestCommand(id), ct);
    }

    private static Task<Response> Reject(
        HttpContext http, SystemId id, RejectFriendRequestCommandHandler rejectHandler, CancellationToken ct)
    {
        if (http.RejectIfSelf(id, "You cannot reject a friend request from yourself.", ErrorCodes.CannotRejectSelf) is { } reject)
            return Task.FromResult<Response>(reject);

        return http.DispatchNoContentAsync(rejectHandler, OperationIds.FriendRequestReject, new RejectFriendRequestCommand(id), ct);
    }
}
