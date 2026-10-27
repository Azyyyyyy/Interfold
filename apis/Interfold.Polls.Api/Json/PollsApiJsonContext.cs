using System.Text.Json.Serialization;
using Interfold.Polls.Contracts.Models.Read;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;

namespace Interfold.Polls.Api.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(SuccessResponse<PollReadModel>))]
[JsonSerializable(typeof(SuccessResponse<IReadOnlyList<PollReadModel>>))]
public sealed partial class PollsApiJsonContext : JsonSerializerContext;
