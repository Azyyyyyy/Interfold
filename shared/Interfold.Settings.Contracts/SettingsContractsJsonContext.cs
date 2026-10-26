using System.Text.Json.Serialization;
using Interfold.Settings.Contracts.Configuration;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Settings.Contracts.Models.Read;
using Interfold.Settings.Contracts.Models.Wire;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Socket.Contracts;

namespace Interfold.Settings.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(SettingsUsernameRequest))]
[JsonSerializable(typeof(SettingsDescriptionRequest))]
[JsonSerializable(typeof(SettingsPushTokenRequest))]
[JsonSerializable(typeof(SettingsEncryptionRequest))]
[JsonSerializable(typeof(SettingsImportRequest))]
[JsonSerializable(typeof(SettingsCreateFieldRequest))]
[JsonSerializable(typeof(SettingsUpdateFieldRequest))]
[JsonSerializable(typeof(SettingsRelocateFieldRequest))]
[JsonSerializable(typeof(LinkTokenReadModel))]
[JsonSerializable(typeof(EncryptionKeyResponse))]
[JsonSerializable(typeof(FieldCreatedResponse))]
[JsonSerializable(typeof(ImportDispatchResponse))]
[JsonSerializable(typeof(DiscordBotStatusResponse))]
[JsonSerializable(typeof(FirebaseClientConfigResponse))]
[JsonSerializable(typeof(FirebaseAndroidConfigResponse))]
[JsonSerializable(typeof(FirebaseIosConfigResponse))]
[JsonSerializable(typeof(FirebaseWebConfigResponse))]
[JsonSerializable(typeof(FirebaseAndroidClientConfig))]
[JsonSerializable(typeof(FirebaseIosClientConfig))]
[JsonSerializable(typeof(FirebaseWebClientConfig))]
[JsonSerializable(typeof(ImportFormat[]))]
[JsonSerializable(typeof(ExportFormat[]))]
[JsonSerializable(typeof(SpImportPayload))]
[JsonSerializable(typeof(PkImportPayload))]
[JsonSerializable(typeof(SettingsFieldsUpdatedPayload))]
[JsonSerializable(typeof(SettingsUsernameUpdatedPayload))]
[JsonSerializable(typeof(SettingsSelfUpdatedPayload))]
[JsonSerializable(typeof(DiscordAccountLinkedPayload))]
[JsonSerializable(typeof(GoogleAccountLinkedPayload))]
[JsonSerializable(typeof(AppleAccountLinkedPayload))]
[JsonSerializable(typeof(ImportCompletedSocketPayload))]
[JsonSerializable(typeof(PhxFrame<SettingsFieldsUpdatedPayload>))]
[JsonSerializable(typeof(PhxFrame<SettingsUsernameUpdatedPayload>))]
[JsonSerializable(typeof(PhxFrame<SettingsSelfUpdatedPayload>))]
[JsonSerializable(typeof(PhxFrame<DiscordAccountLinkedPayload>))]
[JsonSerializable(typeof(PhxFrame<GoogleAccountLinkedPayload>))]
[JsonSerializable(typeof(PhxFrame<AppleAccountLinkedPayload>))]
[JsonSerializable(typeof(PhxFrame<ImportCompletedSocketPayload>))]
public sealed partial class SettingsContractsJsonContext : JsonSerializerContext;
