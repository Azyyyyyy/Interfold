using System.Text.Json.Serialization;
using Interfold.Settings.Contracts.Models.Commands;

namespace Interfold.Settings.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateUsernameCommand))]
[JsonSerializable(typeof(UpdateDescriptionCommand))]
[JsonSerializable(typeof(AddPushTokenCommand))]
[JsonSerializable(typeof(RemovePushTokenCommand))]
[JsonSerializable(typeof(UnlinkDiscordCommand))]
[JsonSerializable(typeof(UnlinkEmailCommand))]
[JsonSerializable(typeof(UnlinkAppleCommand))]
[JsonSerializable(typeof(DeleteAccountCommand))]
[JsonSerializable(typeof(WipeAltersCommand))]
[JsonSerializable(typeof(WipeTagsCommand))]
[JsonSerializable(typeof(CreateLinkTokenCommand))]
[JsonSerializable(typeof(UploadAvatarCommand))]
[JsonSerializable(typeof(DeleteAvatarCommand))]
[JsonSerializable(typeof(SetupEncryptionCommand))]
[JsonSerializable(typeof(RecoverEncryptionCommand))]
[JsonSerializable(typeof(ResetEncryptionCommand))]
[JsonSerializable(typeof(CreateFieldCommand))]
[JsonSerializable(typeof(UpdateFieldCommand))]
[JsonSerializable(typeof(DeleteFieldCommand))]
[JsonSerializable(typeof(RelocateFieldCommand))]
[JsonSerializable(typeof(AccountCommandResult))]
[JsonSerializable(typeof(SettingsCommandResult))]
[JsonSerializable(typeof(SettingsFieldCommandResult))]
[JsonSerializable(typeof(EncryptionCommandResult))]
[JsonSerializable(typeof(ImportDispatchCommandResult))]
public sealed partial class SettingsCommandJsonContext : JsonSerializerContext;
