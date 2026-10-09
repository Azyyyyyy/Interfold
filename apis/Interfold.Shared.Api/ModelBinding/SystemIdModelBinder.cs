using System.Security.Claims;
using Interfold.Shared.Api.Middleware;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Interfold.Shared.Api.ModelBinding;

/// <summary>
/// Custom MVC model binder for <see cref="SystemId"/>. Resolves the sentinel value <c>"me"</c>
/// to the authenticated user's <see cref="SystemId"/> via <see cref="InterfoldPrincipalMiddleware.PrincipalIdItemKey"/>
/// (or JWT claims fallback), transparently supplying the caller's system identity to controllers.
/// For any other value, strips legacy region prefixes and binds via <see cref="SystemId.TryParse"/>.
/// </summary>
public sealed class SystemIdModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var valueProviderResult = bindingContext.ValueProvider.GetValue(bindingContext.FieldName);
        if (valueProviderResult == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }

        bindingContext.ModelState.SetModelValue(bindingContext.FieldName, valueProviderResult);
        var raw = valueProviderResult.FirstValue;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Task.CompletedTask;
        }

        if (string.Equals(raw, "me", StringComparison.OrdinalIgnoreCase))
        {
            if (bindingContext.HttpContext.Items.TryGetValue(InterfoldPrincipalMiddleware.PrincipalIdItemKey, out var item)
                && item is SystemId principalId)
            {
                bindingContext.Result = ModelBindingResult.Success(principalId);
                return Task.CompletedTask;
            }

            var sub = bindingContext.HttpContext.User.FindFirst(JwtClaimNames.Sub)?.Value
                      ?? bindingContext.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrWhiteSpace(sub))
            {
                var stripped = SystemId.StripRegionPrefix(sub);
                if (!string.IsNullOrWhiteSpace(stripped))
                {
                    bindingContext.Result = ModelBindingResult.Success(new SystemId(stripped));
                    return Task.CompletedTask;
                }
            }

            bindingContext.ModelState.TryAddModelError(
                bindingContext.FieldName,
                "Cannot resolve 'me' for an unauthenticated request.");
            bindingContext.Result = ModelBindingResult.Failed();
            return Task.CompletedTask;
        }

        if (SystemId.TryParse(raw, provider: null, out var parsed))
        {
            bindingContext.Result = ModelBindingResult.Success(parsed);
            return Task.CompletedTask;
        }

        bindingContext.ModelState.TryAddModelError(
            bindingContext.FieldName,
            $"The value '{raw}' is not a valid SystemId.");
        bindingContext.Result = ModelBindingResult.Failed();
        return Task.CompletedTask;
    }
}
