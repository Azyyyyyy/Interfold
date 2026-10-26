using System.Text.Json.Serialization;
using Interfold.Fronting.Contracts.Models.Read;
using Interfold.Shared.Contracts;
using Interfold.Socket.Contracts;

namespace Interfold.Fronting.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(FrontActiveReadModel))]
[JsonSerializable(typeof(FrontHistoryReadModel))]
[JsonSerializable(typeof(IReadOnlyList<FrontActiveReadModel>))]
[JsonSerializable(typeof(IReadOnlyList<FrontHistoryReadModel>))]
[JsonSerializable(typeof(FrontBulkUpdateRequest))]
[JsonSerializable(typeof(FrontStartEntry))]
[JsonSerializable(typeof(FrontStartRequest))]
[JsonSerializable(typeof(FrontEndRequest))]
[JsonSerializable(typeof(FrontPrimaryRequest))]
[JsonSerializable(typeof(FrontSetRequest))]
[JsonSerializable(typeof(FrontCommentRequest))]
[JsonSerializable(typeof(FrontStartedResponse))]
[JsonSerializable(typeof(SocketBatchedFrontsPayload))]
[JsonSerializable(typeof(FrontSocketPayload))]
[JsonSerializable(typeof(FrontsSocketPayload))]
[JsonSerializable(typeof(FrontIdSocketPayload))]
[JsonSerializable(typeof(PhxFrame<SocketBatchedFrontsPayload>))]
[JsonSerializable(typeof(PhxFrame<FrontSocketPayload>))]
[JsonSerializable(typeof(PhxFrame<FrontsSocketPayload>))]
[JsonSerializable(typeof(PhxFrame<FrontIdSocketPayload>))]
public sealed partial class FrontingContractsJsonContext : JsonSerializerContext;
