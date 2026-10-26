using Interfold.Shared.Api.Http;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Operations;
using Interfold.Tags.Contracts.Ids;
using Interfold.Tags.Contracts.Models.Commands;
using Interfold.Tags.Contracts.Models.Read;
using Interfold.Tags.Domain;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Tags.Api.DependencyInjection;

public static partial class TagsModuleServiceCollectionExtensions
{
    public static IEndpointRouteBuilder MapTagsModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapInterfoldGroup("/api/systems/me/tags");
        group.MapPost("/", CreateTag);
        group.MapPatch("/{id}", UpdateTag);
        group.MapDelete("/{id}", DeleteTag);
        group.MapPost("/{id}/alter", AttachAlter);
        group.MapDelete("/{id}/alter", DetachAlter);
        group.MapPost("/{id}/parent", SetParent);
        group.MapDelete("/{id}/parent", RemoveParent);
        return endpoints;
    }

    private static async Task<Response<TagReadModel>> CreateTag(
        HttpContext http,
        CreateTagRequest body,
        ITagRepository tagRepository,
        CreateTagCommandHandler create,
        CancellationToken cancellationToken)
    {
        var principal = http.PrincipalId();
        return await http.DispatchCreatedAsync(
            create,
            OperationIds.TagCreate,
            new CreateTagCommand(body.Name, body.ParentTagId, InsertedAtUtc: default),
            async (res) => await tagRepository.GetAsync(principal, res.TagId, cancellationToken),
            cancellationToken);
    }

    private static Task<Response> UpdateTag(
        HttpContext http, TagId id, UpdateTagRequest body, UpdateTagCommandHandler update, CancellationToken ct)
        => http.DispatchNoContentAsync(update, OperationIds.TagUpdate, new UpdateTagCommand(id, body.Name, body.Color, body.Description, body.SecurityLevel), ct);

    private static Task<Response> DeleteTag(
        HttpContext http, TagId id, DeleteTagCommandHandler delete, CancellationToken ct)
        => http.DispatchNoContentAsync(delete, OperationIds.TagDelete, new DeleteTagCommand(id), ct);

    private static Task<Response> AttachAlter(
        HttpContext http, TagId id, TagAlterRequest body, AttachAlterToTagCommandHandler attachAlter, CancellationToken ct)
        => InterfoldHttp.InvalidAlterId(body.AlterId) is { } invalid
            ? Task.FromResult<Response>(invalid)
            : http.DispatchNoContentAsync(attachAlter, OperationIds.TagAttachAlter, new AttachAlterToTagCommand(id, body.AlterId), ct);

    private static Task<Response> DetachAlter(
        HttpContext http, TagId id, TagAlterRequest body, DetachAlterFromTagCommandHandler detachAlter, CancellationToken ct)
        => InterfoldHttp.InvalidAlterId(body.AlterId) is { } invalid
            ? Task.FromResult<Response>(invalid)
            : http.DispatchNoContentAsync(detachAlter, OperationIds.TagDetachAlter, new DetachAlterFromTagCommand(id, body.AlterId), ct);

    private static Task<Response> SetParent(
        HttpContext http, TagId id, SetParentRequest body, SetParentTagCommandHandler setParent, CancellationToken ct)
    {
        if (body.ParentTagId is not { } parentTagId || parentTagId == TagId.Empty)
            return Task.FromResult<Response>(new ErrorResponse("Invalid parent tag ID.", ErrorCodes.InvalidParentTagId, System.Net.HttpStatusCode.BadRequest));

        return http.DispatchNoContentAsync(setParent, OperationIds.TagSetParent, new SetParentTagCommand(id, parentTagId), ct);
    }

    private static Task<Response> RemoveParent(
        HttpContext http, TagId id, RemoveParentTagCommandHandler removeParent, CancellationToken ct)
        => http.DispatchNoContentAsync(removeParent, OperationIds.TagRemoveParent, new RemoveParentTagCommand(id), ct);
}
