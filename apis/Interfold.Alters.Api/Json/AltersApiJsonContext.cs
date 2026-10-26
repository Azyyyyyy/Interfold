using System.Text.Json.Serialization;
using Interfold.Alters.Contracts.Models;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;

namespace Interfold.Alters.Api.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(SuccessResponse<AlterReadModel>))]
[JsonSerializable(typeof(SuccessResponse<IReadOnlyList<AlterReadModel>>))]
public sealed partial class AltersApiJsonContext : JsonSerializerContext;
