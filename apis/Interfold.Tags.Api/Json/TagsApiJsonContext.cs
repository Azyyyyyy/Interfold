using System.Text.Json.Serialization;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Tags.Contracts.Models.Read;

namespace Interfold.Tags.Api.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(SuccessResponse<TagReadModel>))]
[JsonSerializable(typeof(SuccessResponse<TagPublicReadModel>))]
[JsonSerializable(typeof(SuccessResponse<IReadOnlyList<TagPublicReadModel>>))]
public sealed partial class TagsApiJsonContext : JsonSerializerContext;
