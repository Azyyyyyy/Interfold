using System.Text.Json.Serialization;
using Interfold.Fronting.Contracts.Models.Read;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;

namespace Interfold.Fronting.Api.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(SuccessResponse<FrontStartedResponse>))]
[JsonSerializable(typeof(SuccessResponse<FrontActiveReadModel>))]
[JsonSerializable(typeof(SuccessResponse<IReadOnlyList<FrontActiveReadModel>>))]
[JsonSerializable(typeof(SuccessResponse<IReadOnlyList<FrontHistoryReadModel>>))]
public sealed partial class FrontingApiJsonContext : JsonSerializerContext;
