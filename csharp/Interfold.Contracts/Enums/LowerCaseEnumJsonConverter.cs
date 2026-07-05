using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Reusable <see cref="JsonStringEnumConverter{TEnum}"/> variant that emits enum names as their
/// snake-case-lower spelling on the wire. Because every enum this project ships uses single-word,
/// already-lowercase-friendly names (<c>Single</c>, <c>Primary</c>, <c>Nam</c>, …), the resulting
/// JSON is identical to the pre-typed string layout — <c>"single"</c>, <c>"primary"</c>,
/// <c>"nam"</c> — which is the wire-compatibility contract the strong-typing work has to preserve.
/// </summary>
/// <remarks>
/// The naming policy is applied to reads as well as writes: <see cref="JsonStringEnumConverter{TEnum}"/>
/// respects the same policy for parsing incoming values, and case-insensitive parsing is enabled by
/// default so operator-hand-edited JSON with mixed casing still binds.
/// </remarks>
public sealed class LowerCaseEnumJsonConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.SnakeCaseLower)
    where TEnum : struct, Enum;
