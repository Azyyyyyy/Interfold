using System.Text.Json.Serialization;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Shared.Api.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(SuccessResponse<string>))]
[JsonSerializable(typeof(SuccessResponse<SettingsFieldReadModel>))]
[JsonSerializable(typeof(SuccessResponse<IReadOnlyList<SettingsFieldReadModel>>))]
[JsonSerializable(typeof(SuccessResponse<AccountPublicProfileReadModel>))]
public sealed partial class SharedApiJsonContext : JsonSerializerContext;
