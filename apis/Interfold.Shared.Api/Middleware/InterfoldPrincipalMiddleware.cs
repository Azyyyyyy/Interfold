using System.Security.Claims;
using Interfold.Shared.Api.Controllers.Base;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Interfold.Shared.Api.Middleware;

/// <summary>
/// Resolves and validates principal IDs for Interfold API controllers.
/// </summary>
public sealed class InterfoldPrincipalMiddleware(RequestDelegate next)
{
    internal const string PrincipalIdItemKey = "Interfold.PrincipalId";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!RequiresInterfoldPrincipal(context))
        {
            await next(context);
            return;
        }

        var principalId = ResolvePrincipalId(context.User);
        if (principalId is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Items[PrincipalIdItemKey] = principalId;
        await next(context);
    }

    private static bool RequiresInterfoldPrincipal(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
            return false;

        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return false;

        var actionDescriptor = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        return actionDescriptor is not null
               && typeof(InterfoldControllerBase).IsAssignableFrom(actionDescriptor.ControllerTypeInfo.AsType());
    }

    /// <summary>
    /// The JWT <c>sub</c> claim is the one place a scoped-system-id string crosses the
    /// trust boundary into the process. We tolerate legacy region-scoped prefixes
    /// (e.g. "nam:abcdefg") by stripping them before storing the principal ID so all downstream
    /// services see clean raw system IDs.
    /// </summary>
    private static SystemId? ResolvePrincipalId(ClaimsPrincipal user)
    {
        var sub = user.FindFirst(JwtClaimNames.Sub)?.Value
                  ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(sub))
        {
            return null;
        }

        var rawSub = SystemId.StripRegionPrefix(sub);
        return string.IsNullOrWhiteSpace(rawSub) ? null : new SystemId(rawSub);
    }
}
