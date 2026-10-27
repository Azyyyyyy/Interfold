using System.Text.Json.Serialization;
using Interfold.Infrastructure.Coordination;
using Interfold.Settings.Contracts.Models.Read;
using Interfold.Settings.Contracts.Models.Wire;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Settings.Api.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(SuccessResponse<LinkTokenReadModel>))]
[JsonSerializable(typeof(SuccessResponse<EncryptionKeyResponse>))]
[JsonSerializable(typeof(SuccessResponse<ImportDispatchResponse>))]
[JsonSerializable(typeof(SuccessResponse<FieldCreatedResponse>))]
[JsonSerializable(typeof(SuccessResponse<FirebaseClientConfigResponse>))]
[JsonSerializable(typeof(SuccessResponse<DiscordBotStatusResponse>))]
[JsonSerializable(typeof(SuccessResponse<ImportFormat[]>))]
[JsonSerializable(typeof(SuccessResponse<ExportFormat[]>))]
[JsonSerializable(typeof(SuccessResponse<IReadOnlyList<FcmSendAttempt>>))]
[JsonSerializable(typeof(FcmSendAttempt))]
public sealed partial class SettingsApiJsonContext : JsonSerializerContext;
