using System.Net;
using System.Security.Claims;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Api.Http;

/// <summary>Rewrites route/query sentinel <c>me</c> to the authenticated principal.</summary>
public static class SystemIdBinding
{
    public static void RewriteMe(HttpContext context)
    {
        foreach (var key in context.Request.RouteValues.Keys.ToArray())
        {
            if (context.Request.RouteValues[key] is string route && IsMe(route))
                context.Request.RouteValues[key] = ResolveMeOrThrow(context).Value;
        }

        if (context.Request.Query.Count == 0)
            return;

        List<KeyValuePair<string, string?>>? rewritten = null;
        foreach (var pair in context.Request.Query)
        {
            foreach (var value in pair.Value)
            {
                if (value is not null && IsMe(value))
                {
                    rewritten ??= CopyQuery(context.Request.Query);
                    break;
                }
            }
        }

        if (rewritten is null)
            return;

        var principal = ResolveMeOrThrow(context).Value;
        for (var i = 0; i < rewritten.Count; i++)
        {
            if (rewritten[i].Value is { } value && IsMe(value))
                rewritten[i] = new KeyValuePair<string, string?>(rewritten[i].Key, principal);
        }

        context.Request.QueryString = QueryString.Create(rewritten);
    }

    private static bool IsMe(string value)
        => string.Equals(value, "me", StringComparison.OrdinalIgnoreCase);

    private static List<KeyValuePair<string, string?>> CopyQuery(IQueryCollection query)
    {
        var copy = new List<KeyValuePair<string, string?>>();
        foreach (var pair in query)
        {
            foreach (var value in pair.Value)
                copy.Add(new KeyValuePair<string, string?>(pair.Key, value));
        }

        return copy;
    }

    private static SystemId ResolveMeOrThrow(HttpContext context)
    {
        if (context.Items.TryGetValue(InterfoldHttpContextKeys.PrincipalId, out var item) && item is SystemId fromItems)
            return fromItems;

        var sub = context.User.FindFirst(JwtClaimNames.Sub)?.Value
                  ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(sub))
        {
            var stripped = SystemId.StripRegionPrefix(sub);
            if (!string.IsNullOrWhiteSpace(stripped))
                return new SystemId(stripped);
        }

        throw new InterfoldException(
            "Cannot resolve 'me' for an unauthenticated request.",
            ErrorCodes.BadRequest,
            HttpStatusCode.BadRequest);
    }
}

public sealed class SystemIdMeMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        SystemIdBinding.RewriteMe(context);
        return next(context);
    }
}
