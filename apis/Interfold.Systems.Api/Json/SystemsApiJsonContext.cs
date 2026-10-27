using System.Text.Json.Serialization;
using Interfold.Alters.Contracts.Models;
using Interfold.Fronting.Contracts.Models.Read;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Systems.Contracts.Models.Read;
using Interfold.Tags.Contracts.Models.Read;

namespace Interfold.Systems.Api.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(SuccessResponse<PublicSystemReadModel>))]
[JsonSerializable(typeof(SuccessResponse<PublicSystemBatchReadModel>))]
[JsonSerializable(typeof(SuccessResponse<BareAlter>))]
[JsonSerializable(typeof(SuccessResponse<IReadOnlyList<BareAlter>>))]
[JsonSerializable(typeof(SuccessResponse<TagPublicReadModel>))]
[JsonSerializable(typeof(SuccessResponse<IReadOnlyList<TagPublicReadModel>>))]
[JsonSerializable(typeof(SuccessResponse<IReadOnlyList<FrontActiveReadModel>>))]
public sealed partial class SystemsApiJsonContext : JsonSerializerContext;
