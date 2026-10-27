using Interfold.Fronting.Contracts.Ids;
using Interfold.Fronting.Contracts.Models.Commands;
using Interfold.Fronting.Contracts.Models.Read;
using Interfold.Fronting.Domain;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Shared.Api.Http;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Operations;

namespace Interfold.Fronting.Api.DependencyInjection;

public static partial class FrontingModuleServiceCollectionExtensions
{
    private const string EndAnchor = "end_anchor";
    private const string Start = "start";
    private const string End = "end";

    public static IEndpointRouteBuilder MapFrontingModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapInterfoldGroup("/api/systems/me/front");
        group.MapPost("/", Update);
        group.MapPost("/start", StartFront);
        group.MapPost("/end", EndFront);
        group.MapPost("/set", SetFront);
        group.MapPost("/primary", Primary);
        group.MapGet("/month", Month);
        group.MapGet("/between", Between);
        group.MapGet("/{id}", Show);
        group.MapDelete("/{id}", Delete);
        group.MapPost("/{id}/comment", UpdateComment);
        return endpoints;
    }

    private static Task<Response> Update(
        HttpContext http, FrontBulkUpdateRequest req, BulkUpdateFrontCommandHandler bulkUpdateHandler, CancellationToken ct)
    {
        var payload = new BulkUpdateFrontCommand(
            req.Start.Select(x => new FrontStartItem(x.AlterId, x.Comment)).ToArray(),
            req.End.ToArray());
        return http.DispatchNoContentAsync(bulkUpdateHandler, OperationIds.FrontBulkUpdate, payload, ct);
    }

    private static async Task<Response<FrontStartedResponse>> StartFront(
        HttpContext http, FrontStartRequest req, StartFrontCommandHandler startHandler, CancellationToken ct)
    {
        if (InterfoldHttp.InvalidAlterId(req.Id) is { } invalid)
            return invalid;

        var envelope = http.BuildEnvelope(OperationIds.FrontStart, new StartFrontCommand(req.Id, req.Comment));
        var execution = await startHandler.HandleAsync(envelope, ct);
        var response = http.CommandCreated(execution, r => new FrontStartedResponse(r.FrontId!.Value));

        if (response.IsSuccess && response.AsSuccess.Data.FrontId != FrontId.Empty)
            http.Response.Headers.Location = $"/api/systems/me/front/{response.AsSuccess.Data.FrontId}";

        return response;
    }

    private static Task<Response> EndFront(
        HttpContext http, FrontEndRequest req, EndFrontCommandHandler endHandler, CancellationToken ct)
        => InterfoldHttp.InvalidAlterId(req.Id) is { } invalid
            ? Task.FromResult<Response>(invalid)
            : http.DispatchNoContentAsync(endHandler, OperationIds.FrontEnd, new EndFrontCommand(req.Id), ct);

    private static Task<Response> SetFront(
        HttpContext http, FrontSetRequest req, SetFrontCommandHandler setHandler, CancellationToken ct)
        => InterfoldHttp.InvalidAlterId(req.Id) is { } invalid
            ? Task.FromResult<Response>(invalid)
            : http.DispatchNoContentAsync(setHandler, OperationIds.FrontSet, new SetFrontCommand(req.Id, req.Comment), ct);

    private static Task<Response> Primary(
        HttpContext http, FrontPrimaryRequest req, SetPrimaryFrontCommandHandler primaryHandler, CancellationToken ct)
        => InterfoldHttp.InvalidAlterId(req.Id, allowNull: true) is { } invalid
            ? Task.FromResult<Response>(invalid)
            : http.DispatchNoContentAsync(primaryHandler, OperationIds.FrontPrimary, new SetPrimaryFrontCommand(req.Id), ct);

    private static async Task<Response<IReadOnlyList<FrontHistoryReadModel>>> Month(
        HttpContext http, IFrontingRepository repository, CancellationToken ct)
    {
        var endAnchor = UnixSecondsQuery.Bind(
            http, EndAnchor, "invalid_end_anchor",
            "Invalid end anchor. Please pass a valid Unix timestamp.");
        if (endAnchor.Error is { } error)
            return error;

        var end = endAnchor.Value.ToDateTimeOffset();
        var start = end.AddDays(-30);
        var fronts = await repository.ListHistoryBetweenAsync(http.PrincipalId(), start, end, ct);
        return new SuccessResponse<IReadOnlyList<FrontHistoryReadModel>>(fronts);
    }

    private static async Task<Response<IReadOnlyList<FrontHistoryReadModel>>> Between(
        HttpContext http, IFrontingRepository repository, CancellationToken ct)
    {
        var startAnchor = UnixSecondsQuery.Bind(
            http, Start, "invalid_anchor",
            "Invalid start anchor. Please pass a valid Unix timestamp.");
        if (startAnchor.Error is { } startError)
            return startError;

        var endAnchor = UnixSecondsQuery.Bind(
            http, End, "invalid_anchor",
            "Invalid end anchor. Please pass a valid Unix timestamp.");
        if (endAnchor.Error is { } endError)
            return endError;

        var fronts = await repository.ListHistoryBetweenAsync(
            http.PrincipalId(),
            startAnchor.Value.ToDateTimeOffset(),
            endAnchor.Value.ToDateTimeOffset(),
            ct);
        return new SuccessResponse<IReadOnlyList<FrontHistoryReadModel>>(fronts);
    }

    private static async Task<Response<FrontActiveReadModel>> Show(
        HttpContext http, FrontId id, IFrontingRepository repository, CancellationToken ct)
    {
        var front = await repository.GetActiveByFrontIdAsync(http.PrincipalId(), id, ct);
        return InterfoldHttp.OkOrNotFound(front, "Front not found.", ErrorCodes.FrontNotFound);
    }

    private static Task<Response> Delete(
        HttpContext http, FrontId id, DeleteFrontByIdCommandHandler deleteByIdHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(deleteByIdHandler, OperationIds.FrontDelete, new DeleteFrontByIdCommand(id), ct);

    private static Task<Response> UpdateComment(
        HttpContext http, FrontId id, FrontCommentRequest req, UpdateFrontCommentCommandHandler updateCommentHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(updateCommentHandler, OperationIds.FrontCommentUpdate, new UpdateFrontCommentCommand(id, req.Comment), ct);
}
