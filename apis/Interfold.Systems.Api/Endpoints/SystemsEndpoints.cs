using System.Diagnostics;
using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Fronting.Contracts.Models.Read;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Api.Helpers;
using Interfold.Shared.Api.Http;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Interfold.Systems.Api.Filters;
using Interfold.Systems.Contracts.Models.Read;
using Interfold.Tags.Contracts.Ids;
using Interfold.Tags.Contracts.Models.Read;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Systems.Api.DependencyInjection;

public static partial class SystemsModuleServiceCollectionExtensions
{
    public static IEndpointRouteBuilder MapSystemsModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapInterfoldGroup("/api/systems/{systemId}");
        group.MapGet("/", Show);
        group.MapGet("/alters", ListAlters).AddEndpointFilter<SystemMustExistAttribute>();
        group.MapGet("/alters/{alterId}", ShowAlter).AddEndpointFilter<SystemMustExistAttribute>();
        group.MapGet("/tags", ListTags).AddEndpointFilter<SystemMustExistAttribute>();
        group.MapGet("/tags/{tagId}", ShowTag).AddEndpointFilter<SystemMustExistAttribute>();
        group.MapGet("/fronting", ListFronting).AddEndpointFilter<SystemMustExistAttribute>();
        group.MapGet("/batch", Batch).AddEndpointFilter<SystemMustExistAttribute>();
        return endpoints;
    }

    private static async Task<Response<PublicSystemReadModel>> Show(
        HttpContext http, SystemId systemId, IAccountRepository accounts, CancellationToken ct)
    {
        var model = await accounts.GetPublicSystemAsync(systemId, ct);
        if (model is not null)
            model = model with { AvatarUrl = http.QualifyAvatar(model) };

        return InterfoldHttp.OkOrNotFound(model, "System not found.", ErrorCodes.SystemNotFound);
    }

    private static async Task<Response<IReadOnlyList<BareAlter>>> ListAlters(
        HttpContext http, SystemId systemId, IAlterRepository alters, CancellationToken ct)
    {
        var list = await alters.ListGuardedAsync(systemId, http.PrincipalId(), ct);
        foreach (var a in list) a.AvatarUrl = http.QualifyAvatar(a);
        return new SuccessResponse<IReadOnlyList<BareAlter>>(list);
    }

    private static async Task<Response<BareAlter>> ShowAlter(
        HttpContext http, SystemId systemId, AlterId alterId, IAlterRepository alters, CancellationToken ct)
    {
        var alter = await alters.GetGuardedAsync(systemId, alterId, http.PrincipalId(), ct);
        if (alter is not null)
            alter.AvatarUrl = http.QualifyAvatar(alter);

        return InterfoldHttp.OkOrNotFound(alter, "Alter not found.", ErrorCodes.AlterNotFound);
    }

    private static async Task<Response<IReadOnlyList<TagPublicReadModel>>> ListTags(
        HttpContext http, SystemId systemId, ITagRepository tags, CancellationToken ct)
    {
        var list = await tags.ListGuardedAsync(systemId, http.PrincipalId(), ct);
        return new SuccessResponse<IReadOnlyList<TagPublicReadModel>>(list);
    }

    private static async Task<Response<TagPublicReadModel>> ShowTag(
        HttpContext http, SystemId systemId, TagId tagId, ITagRepository tags, CancellationToken ct)
    {
        var tag = await tags.GetGuardedAsync(systemId, tagId, http.PrincipalId(), ct);
        return InterfoldHttp.OkOrNotFound(tag, "Tag not found.", ErrorCodes.TagNotFound);
    }

    private static async Task<Response<IReadOnlyList<FrontActiveReadModel>>> ListFronting(
        HttpContext http, SystemId systemId, IFrontingRepository fronting, CancellationToken ct)
    {
        var fronts = await fronting.ListActiveGuardedAsync(systemId, http.PrincipalId(), ct);
        return new SuccessResponse<IReadOnlyList<FrontActiveReadModel>>(fronts);
    }

    private static async Task<Response<PublicSystemBatchReadModel>> Batch(
        HttpContext http,
        SystemId systemId,
        IAlterRepository alters,
        ITagRepository tags,
        IFriendshipRepository friendships,
        CancellationToken ct)
    {
        var principalId = http.PrincipalId();
        if (principalId.RepresentsSameUserAs(systemId))
        {
            return new ErrorResponse(
                "You cannot view your own system through this endpoint.",
                ErrorCodes.InvalidEndpoint,
                System.Net.HttpStatusCode.Forbidden);
        }

        var aggregateSw = Stopwatch.StartNew();
        var altersTask = TimeSliceAsync("alter", alters.ListGuardedAsync(systemId, principalId, ct));
        var tagsTask = TimeSliceAsync("tag", tags.ListGuardedAsync(systemId, principalId, ct));
        var friendshipTask = friendships.GetFriendshipAsync(principalId, systemId, ct);

        await Task.WhenAll(altersTask, tagsTask, friendshipTask);
        InterfoldMetrics.GuardedBatchLatencyMs.Record(aggregateSw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("slice", "aggregate"));

        var batchAlters = altersTask.Result;
        foreach (var a in batchAlters) a.AvatarUrl = http.QualifyAvatar(a);

        var friendship = friendshipTask.Result;
        if (friendship is not null)
        {
            friendship = friendship with
            {
                Friend = friendship.Friend with
                {
                    AvatarUrl = AvatarUrlQualifier.QualifyAvatar(friendship.Friend, http.Request.Scheme, http.Request.Host),
                },
                Fronting = friendship.Fronting
                    .Select(f => f with
                    {
                        Alter = f.Alter with
                        {
                            AvatarUrl = AvatarUrlQualifier.QualifyAvatar(f.Alter, http.Request.Scheme, http.Request.Host),
                        },
                    })
                    .ToArray(),
            };
        }

        return new PublicSystemBatchReadModel(
            Friendship: friendship,
            Tags: tagsTask.Result,
            Alters: batchAlters);
    }

    private static async Task<T> TimeSliceAsync<T>(string slice, Task<T> sliceTask)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await sliceTask.ConfigureAwait(false);
        }
        finally
        {
            InterfoldMetrics.GuardedBatchLatencyMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("slice", slice));
        }
    }
}
