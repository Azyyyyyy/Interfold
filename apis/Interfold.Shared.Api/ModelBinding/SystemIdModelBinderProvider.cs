using Interfold.Shared.Contracts.Ids;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Interfold.Shared.Api.ModelBinding;

/// <summary>
/// Wires <see cref="SystemIdModelBinder"/> into the MVC model binder pipeline for
/// <see cref="SystemId"/>-typed action parameters.
/// </summary>
public sealed class SystemIdModelBinderProvider : IModelBinderProvider
{
    private static readonly SystemIdModelBinder Instance = new();

    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Metadata.ModelType == typeof(SystemId) ? Instance : null;
    }
}
