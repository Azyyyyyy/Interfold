using Interfold.Shared.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Interfold.Shared.Api.Filters;

/// <summary>404 unless <c>OCTOCON_TEST_HOOKS=1</c>. Apply to test-only endpoints
/// so they stay invisible in production.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireTestHooksAttribute : Attribute, IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var hooks = context.HttpContext.RequestServices.GetRequiredService<IOptions<TestHooksConfiguration>>();
        if (!hooks.Value.Enabled)
            return TypedResults.NotFound();

        return await next(context);
    }
}
