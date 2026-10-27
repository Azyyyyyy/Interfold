using System.Net;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Systems.Api.Filters;

/// <summary>Endpoint filter that short-circuits with 404
/// (<see cref="ErrorCodes.SystemNotFound"/>) when the <c>systemId</c> argument doesn't
/// resolve via <see cref="IAccountRepository.GetPublicProfileAsync"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SystemMustExistAttribute : Attribute, IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        SystemId? systemId = null;
        foreach (var arg in context.Arguments)
        {
            if (arg is SystemId bound)
            {
                systemId = bound;
                break;
            }
        }

        if (systemId is null)
            return NotFoundResult();

        var accounts = context.HttpContext.RequestServices.GetRequiredService<IAccountRepository>();
        var profile = await accounts.GetPublicProfileAsync(systemId.Value, context.HttpContext.RequestAborted);
        if (profile is null)
            return NotFoundResult();

        return await next(context);
    }

    private static ErrorResponse NotFoundResult() => new(
        "System not found.",
        ErrorCodes.SystemNotFound,
        HttpStatusCode.NotFound);
}
