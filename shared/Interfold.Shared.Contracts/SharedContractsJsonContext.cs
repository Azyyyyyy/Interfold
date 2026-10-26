using System.Text.Json.Serialization;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Shared.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(ErrorCode))]
[JsonSerializable(typeof(SettingsFieldReadModel))]
[JsonSerializable(typeof(IReadOnlyList<SettingsFieldReadModel>))]
[JsonSerializable(typeof(AccountPublicProfileReadModel))]
[JsonSerializable(typeof(AlterPublicFieldReadModel))]
[JsonSerializable(typeof(IReadOnlyList<AlterPublicFieldReadModel>))]
[JsonSerializable(typeof(AvatarUrlUploadRequest))]
[JsonSerializable(typeof(PatchValue<DateTime>))]
[JsonSerializable(typeof(VisibilityLevel))]
[JsonSerializable(typeof(FieldType))]
[JsonSerializable(typeof(AvatarSource))]
[JsonSerializable(typeof(ClientPlatform))]
[JsonSerializable(typeof(FriendshipLevel))]
[JsonSerializable(typeof(HexColor))]
[JsonSerializable(typeof(SystemId))]
[JsonSerializable(typeof(AlterId))]
public sealed partial class SharedContractsJsonContext : JsonSerializerContext;
