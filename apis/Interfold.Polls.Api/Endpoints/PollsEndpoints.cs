using Interfold.Polls.Contracts.Ids;
using Interfold.Polls.Contracts.Models.Commands;
using Interfold.Polls.Contracts.Models.Read;
using Interfold.Polls.Domain;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Shared.Api.Http;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Operations;

namespace Interfold.Polls.Api.DependencyInjection;

public static partial class PollsModuleServiceCollectionExtensions
{
    public static IEndpointRouteBuilder MapPollsModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapInterfoldGroup("/api/polls");
        group.MapGet("/", Index);
        group.MapGet("/{id}", Show);
        group.MapPost("/", Create);
        group.MapPatch("/{id}", Update);
        group.MapDelete("/{id}", Delete);
        return endpoints;
    }

    private static async Task<Response<IReadOnlyList<PollReadModel>>> Index(
        HttpContext http, IPollRepository pollRepository, CancellationToken ct)
    {
        var polls = await pollRepository.ListAsync(http.PrincipalId(), ct);
        return new SuccessResponse<IReadOnlyList<PollReadModel>>(polls);
    }

    private static async Task<Response<PollReadModel>> Show(
        HttpContext http, PollId id, IPollRepository pollRepository, CancellationToken ct)
    {
        var poll = await pollRepository.GetAsync(http.PrincipalId(), id, ct);
        return InterfoldHttp.OkOrNotFound(poll, "Poll not found.", ErrorCodes.PollNotFound);
    }

    private static async Task<Response<PollReadModel>> Create(
        HttpContext http,
        CreatePollRequest req,
        IPollRepository pollRepository,
        CreatePollCommandHandler create,
        CancellationToken ct)
    {
        var principal = http.PrincipalId();
        return await http.DispatchCreatedAsync(
            create,
            OperationIds.PollCreate,
            new CreatePollCommand(req.Title, req.Description, req.Type ?? PollType.Vote, req.TimeEnd, InsertedAtUtc: default),
            async (res) => await pollRepository.GetAsync(principal, res.PollId, ct),
            ct);
    }

    private static Task<Response> Update(
        HttpContext http, PollId id, UpdatePollRequest req, UpdatePollCommandHandler update, CancellationToken ct)
    {
        if (req.TimeEnd.IsInvalid)
            return Task.FromResult<Response>(new ErrorResponse("Invalid time_end.", ErrorCodes.PollInvalidTimeEnd, System.Net.HttpStatusCode.BadRequest));

        DateTime? resolvedTimeEnd = req.TimeEnd.State == PatchValueState.Value ? req.TimeEnd.Value : null;
        return http.DispatchNoContentAsync(update, OperationIds.PollUpdate, new UpdatePollCommand(id, req.Title, req.Description, resolvedTimeEnd, req.TimeEnd.IsSet, req.Data), ct);
    }

    private static Task<Response> Delete(
        HttpContext http, PollId id, DeletePollCommandHandler delete, CancellationToken ct)
        => http.DispatchNoContentAsync(delete, OperationIds.PollDelete, new DeletePollCommand(id), ct);
}
