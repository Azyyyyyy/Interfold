using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Alters.Contracts.Models.Read;
using Interfold.Alters.Domain;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Shared.Api.Http;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Api.Services;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Contracts.Operations;

namespace Interfold.Alters.Api.DependencyInjection;

public static partial class AltersModuleServiceCollectionExtensions
{
    public static IEndpointRouteBuilder MapAltersModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapInterfoldGroup("/api/systems/me/alters");
        group.MapGet("/", List);
        group.MapGet("/{alterId}", Show);
        group.MapPost("/", Create);
        group.MapPatch("/{alterId}", Update);
        group.MapDelete("/{alterId}", Delete);
        group.MapPut("/{alterId}/avatar", UploadAvatar);
        group.MapDelete("/{alterId}/avatar", DeleteAvatar);
        return endpoints;
    }

    private static async Task<Response<IReadOnlyList<AlterReadModel>>> List(
        HttpContext http, IAlterRepository alterRepository, CancellationToken ct)
    {
        var alters = await alterRepository.ListAsync(http.PrincipalId(), ct);
        return new SuccessResponse<IReadOnlyList<AlterReadModel>>(alters);
    }

    private static async Task<Response<AlterReadModel>> Show(
        HttpContext http, AlterId alterId, IAlterRepository alterRepository, CancellationToken ct)
    {
        var alter = await alterRepository.GetAsync(http.PrincipalId(), alterId, ct);
        if (alter is not null)
            alter.AvatarUrl = http.QualifyAvatar(alter);
        return InterfoldHttp.OkOrNotFound(alter, "Alter not found.", ErrorCodes.AlterNotFound);
    }

    private static async Task<Response<AlterReadModel>> Create(
        HttpContext http,
        CreateAlterRequest req,
        IAlterRepository alterRepository,
        CreateAlterCommandHandler createHandler,
        CancellationToken ct)
    {
        var principal = http.PrincipalId();
        var envelope = http.BuildEnvelope(OperationIds.AlterCreate, new CreateAlterCommand(req.Name, http.Time().GetUtcNow()));

        return await http.CommandCreatedAsync(
            await createHandler.HandleAsync(envelope, ct),
            async res => await alterRepository.GetAsync(principal, res.AlterId, ct),
            locationSelector: res => $"/api/systems/me/alters/{res.AlterId}");
    }

    private static Task<Response> Update(
        HttpContext http, AlterId alterId, UpdateAlterRequest req, UpdateAlterCommandHandler updateHandler, CancellationToken ct)
    {
        var fields = req.Fields?.Select(f => new AlterFieldCommand(f.Id, f.Value)).ToList();
        var payload = new UpdateAlterCommand
        {
            AlterId = alterId,
            Name = req.Name,
            Description = req.Description,
            Color = req.Color,
            Pronouns = req.Pronouns,
            SecurityLevel = req.SecurityLevel,
            Fields = fields,
            ProxyName = req.ProxyName,
            Alias = req.Alias,
            Untracked = req.Untracked,
            Archived = req.Archived,
            Pinned = req.Pinned,
            UpdatedAt = http.Time().GetUtcNow(),
        };

        return http.DispatchNoContentAsync(updateHandler, OperationIds.AlterUpdate, payload, ct);
    }

    private static Task<Response> Delete(
        HttpContext http, AlterId alterId, DeleteAlterCommandHandler deleteHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(deleteHandler, OperationIds.AlterDelete, new DeleteAlterCommand(alterId), ct);

    private static Task<Response> UploadAvatar(
        HttpContext http,
        AlterId alterId,
        IAlterRepository alterRepository,
        UpdateAlterCommandHandler updateHandler,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
    {
        if (http.Request.HasJsonContentType())
            return UploadAvatarByUrl(http, alterId, alterRepository, updateHandler, avatarStorage, ct);

        return http.HandleAvatarUploadAsync(
            async c => await alterRepository.GetAsync(http.PrincipalId(), alterId, c),
            (principal, stream, c) => avatarStorage.SaveAlterAvatarAsync(principal, alterId, stream, c),
            async (url, c) => http.CommandNoContent(await updateHandler.HandleAsync(http.BuildEnvelope(OperationIds.AlterAvatarUpload, new UpdateAlterCommand
            {
                AlterId = alterId,
                AvatarUrl = url,
                AvatarSource = AvatarSource.Local,
                UpdatedAt = http.Time().GetUtcNow(),
            }), c)),
            avatarStorage,
            ct);
    }

    private static async Task<Response> UploadAvatarByUrl(
        HttpContext http,
        AlterId alterId,
        IAlterRepository alterRepository,
        UpdateAlterCommandHandler updateHandler,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
    {
        var req = await http.Request.ReadFromJsonAsync(SharedContractsJsonContext.Default.AvatarUrlUploadRequest, ct);
        if (req is null)
            return new ErrorResponse("Avatar URL payload required.", ErrorCodes.AvatarUrlInvalid, System.Net.HttpStatusCode.BadRequest);

        return await http.HandleAvatarUrlUploadAsync(
            req.Url.Value,
            async c => await alterRepository.GetAsync(http.PrincipalId(), alterId, c),
            async (url, c) => http.CommandNoContent(await updateHandler.HandleAsync(http.BuildEnvelope(OperationIds.AlterAvatarUpload, new UpdateAlterCommand
            {
                AlterId = alterId,
                AvatarUrl = url,
                AvatarSource = AvatarSource.External,
                UpdatedAt = http.Time().GetUtcNow(),
            }), c)),
            avatarStorage,
            ct);
    }

    private static Task<Response> DeleteAvatar(
        HttpContext http,
        AlterId alterId,
        IAlterRepository alterRepository,
        UpdateAlterCommandHandler updateHandler,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
        => http.HandleAvatarDeleteAsync(
            async c => await alterRepository.GetAsync(http.PrincipalId(), alterId, c),
            async c => http.CommandNoContent(await updateHandler.HandleAsync(http.BuildEnvelope(OperationIds.AlterAvatarDelete, new UpdateAlterCommand
            {
                AlterId = alterId,
                UpdatedAt = http.Time().GetUtcNow(),
                ClearAvatar = true,
            }), c)),
            avatarStorage,
            ct);
}
