using Interfold.Shared.Contracts.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Interfold.Shared.Api.Filters;

/// <summary>404 unless <c>OCTOCON_TEST_HOOKS=1</c>. Apply to test-only
/// controllers or actions so they stay invisible in production.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireTestHooksAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var hooks = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<TestHooksConfiguration>>();
        if (!hooks.Value.Enabled)
        {
            context.Result = new NotFoundResult();
            return;
        }

        await next();
    }
}
