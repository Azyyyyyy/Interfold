using System.Collections.Concurrent;
using Interfold.Shared.Contracts.Enums;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Interfold.Shared.Api.ModelBinding;

/// <summary>
/// Model binder provider that uses <see cref="EnumWire{TEnum}.TryParse"/> to bind enum model
/// parameters from wire values (e.g. <c>[JsonStringEnumMemberName]</c> attributes) or member names.
/// </summary>
public sealed class EnumWireModelBinderProvider : IModelBinderProvider
{
    private static readonly ConcurrentDictionary<Type, IModelBinder> Binders = new();

    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var modelType = context.Metadata.ModelType;
        var enumType = Nullable.GetUnderlyingType(modelType) ?? modelType;

        if (!enumType.IsEnum)
        {
            return null;
        }

        return Binders.GetOrAdd(enumType, static t =>
        {
            var binderType = typeof(EnumWireModelBinder<>).MakeGenericType(t);
            return (IModelBinder)Activator.CreateInstance(binderType)!;
        });
    }
}

internal sealed class EnumWireModelBinder<TEnum> : IModelBinder where TEnum : struct, Enum
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

        if (EnumWire<TEnum>.TryParse(raw, out var parsed))
        {
            bindingContext.Result = ModelBindingResult.Success(parsed);
            return Task.CompletedTask;
        }

        bindingContext.ModelState.TryAddModelError(
            bindingContext.FieldName,
            $"The value '{raw}' is not valid for {bindingContext.ModelMetadata.GetDisplayName()}.");
        bindingContext.Result = ModelBindingResult.Failed();
        return Task.CompletedTask;
    }
}
