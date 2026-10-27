using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Contracts.Models.Read;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Fronting.Contracts.Models.Read;
using Interfold.Journals.Contracts.Models.Read;
using Interfold.Polls.Contracts.Models.Read;
using Interfold.Settings.Contracts.Models.Read;
using Interfold.Settings.Contracts.Models.Wire;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Socket.Contracts;
using Interfold.Systems.Contracts.Models.Read;
using Interfold.Tags.Contracts.Models.Read;

namespace Interfold.IntegrationTests.Shared.TestServices;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(TestErrorResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(TestEnvelope<JsonElement>))]
[JsonSerializable(typeof(TestEnvelope<string[]>))]
[JsonSerializable(typeof(TestEnvelope<AlterReadModel>))]
[JsonSerializable(typeof(TestEnvelope<IReadOnlyList<AlterReadModel>>))]
[JsonSerializable(typeof(TestEnvelope<BareAlter>))]
[JsonSerializable(typeof(TestEnvelope<IReadOnlyList<BareAlter>>))]
[JsonSerializable(typeof(TestEnvelope<TagReadModel>))]
[JsonSerializable(typeof(TestEnvelope<IReadOnlyList<TagReadModel>>))]
[JsonSerializable(typeof(TestEnvelope<TagPublicReadModel>))]
[JsonSerializable(typeof(TestEnvelope<IReadOnlyList<TagPublicReadModel>>))]
[JsonSerializable(typeof(TestEnvelope<FrontStartedResponse>))]
[JsonSerializable(typeof(TestEnvelope<FrontActiveReadModel>))]
[JsonSerializable(typeof(TestEnvelope<IReadOnlyList<FrontActiveReadModel>>))]
[JsonSerializable(typeof(TestEnvelope<IReadOnlyList<FrontHistoryReadModel>>))]
[JsonSerializable(typeof(TestEnvelope<JournalReadModel>))]
[JsonSerializable(typeof(TestEnvelope<IReadOnlyList<JournalReadModel>>))]
[JsonSerializable(typeof(TestEnvelope<AlterJournalReadModel>))]
[JsonSerializable(typeof(TestEnvelope<IReadOnlyList<AlterJournalReadModel>>))]
[JsonSerializable(typeof(TestEnvelope<PollReadModel>))]
[JsonSerializable(typeof(TestEnvelope<IReadOnlyList<PollReadModel>>))]
[JsonSerializable(typeof(TestEnvelope<FriendshipReadModel>))]
[JsonSerializable(typeof(TestEnvelope<IReadOnlyList<FriendshipReadModel>>))]
[JsonSerializable(typeof(TestEnvelope<FriendRequestIndexReadModel>))]
[JsonSerializable(typeof(TestEnvelope<PublicSystemReadModel>))]
[JsonSerializable(typeof(TestEnvelope<PublicSystemBatchReadModel>))]
[JsonSerializable(typeof(TestEnvelope<FieldCreatedResponse>))]
[JsonSerializable(typeof(TestEnvelope<ImportDispatchResponse>))]
[JsonSerializable(typeof(TestEnvelope<LinkTokenReadModel>))]
[JsonSerializable(typeof(TestEnvelope<DiscordBotStatusResponse>))]
[JsonSerializable(typeof(TestEnvelope<EncryptionKeyResponse>))]
[JsonSerializable(typeof(TestEnvelope<FirebaseClientConfigResponse>))]
[JsonSerializable(typeof(TestEnvelope<ImportFormat[]>))]
[JsonSerializable(typeof(TestEnvelope<ExportFormat[]>))]
[JsonSerializable(typeof(TestEnvelope<SettingsFieldReadModel>))]
[JsonSerializable(typeof(TestEnvelope<IReadOnlyList<SettingsFieldReadModel>>))]
[JsonSerializable(typeof(TestEnvelope<AccountPublicProfileReadModel>))]
[JsonSerializable(typeof(PhoenixReplyPayload<object>))]
[JsonSerializable(typeof(PhoenixReplyPayload<SocketJoinReconnectPayload>))]
[JsonSerializable(typeof(PhoenixReplyPayload<SocketEndpointProxyResponse>))]
[JsonSerializable(typeof(LoosePhxJoinPayload))]
internal sealed partial class TestJsonContext : JsonSerializerContext;

/// <summary>Join payload with <c>platform</c> as a raw string so unknown spellings (e.g. wasm) stay on the wire.</summary>
public sealed class LoosePhxJoinPayload
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = "";

    [JsonPropertyName("protocolVersion")]
    public string? ProtocolVersion { get; init; }

    [JsonPropertyName("platform")]
    public string? Platform { get; init; }

    [JsonPropertyName("isReconnect")]
    public bool? IsReconnect { get; init; }
}
