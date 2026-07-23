using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;

namespace Interfold.Contracts;

/// <summary>
/// Marker interface for socket event payloads.
/// </summary>
public interface ISocketPayload { }

public sealed record EmptyPayload : ISocketPayload;

public sealed record PhoenixReplyPayload<TResponse>(PhoenixReplyStatus Status, TResponse Response) : ISocketPayload;

public sealed record SocketReasonResponse(ErrorCode Reason) : ISocketPayload;

// Status is HTTP semantics; JsonNumberEnumConverter pins the historical bare-number wire
// form (SocketJson has no enum policy, but explicit is safer than relying on the default).
public sealed record SocketEndpointProxyResponse(
    [property: JsonConverter(typeof(JsonNumberEnumConverter<System.Net.HttpStatusCode>))] System.Net.HttpStatusCode Status,
    string Body) : ISocketPayload;

/// <summary>
/// Typed shape of the <c>endpoint</c> Phoenix frame payload. <c>Body</c> is a raw
/// JSON string (Phoenix wraps the inner API request body as a JSON-string field so
/// it can be forwarded to the loopback API without being re-serialized — see the
/// forwarding comment in <c>WebSocketHandler.HandleEndpointProxyAsync</c>).
/// </summary>
public sealed record SocketEndpointProxyRequest(string? Method, string? Path, string? Body);

// SocketJoinInitPayload + SocketJoinBatchedPayload + SocketBatchedAltersPayload physically
// relocated to host/Interfold.Api/Models/SocketAggregateJoinPayloads.cs during the Phase-3
// Alters slice (namespace preserved for wire-compat). This breaks the spine-batch-DTO cycle
// with Interfold.Alters.Contracts; the three payloads migrate into the Socket module (Phase 3 #10).

public sealed record SocketJoinReconnectPayload(SocketSelfReadModel System) : ISocketPayload;

// SocketBatchedTagsPayload lives in Interfold.Tags.Contracts (Phase-3 migration).
// SocketBatchedFrontsPayload lives in Interfold.Fronting.Contracts (Phase-3 migration).

// The four *Linked members keep the legacy identity-shaped wire names (discord_id etc.)
// but carry only the "SET"/null link-presence flag — see AccountLinkFlag. GoogleLinked is
// historically always null (Google links surface via email) and stays for wire shape.
public sealed record SocketSelfReadModel(
    SystemId Id,
    Username? Username,
    string? Description,
    AvatarUrl? AvatarUrl,
    AvatarSource? AvatarSource,
    [property: JsonPropertyName("discord_id")] AccountLinkFlag DiscordLinked,
    [property: JsonPropertyName("google_id")] AccountLinkFlag GoogleLinked,
    [property: JsonPropertyName("apple_id")] AccountLinkFlag AppleLinked,
    [property: JsonPropertyName("email")] AccountLinkFlag EmailLinked,
    AutoproxyMode AutoproxyMode,
    bool ShowSystemTag,
    int LifetimeAlterCount,
    AlterId? PrimaryFront,
    IReadOnlyList<SettingsFieldReadModel> Fields,
    bool EncryptionInitialized) : ISocketPayload, IAvatarBearing;

// AlterSocketPayload + AlterDeletedSocketPayload + AlterIdSocketPayload live in Interfold.Alters.Contracts (Phase-3 migration).
// TagSocketPayload + TagDeletedSocketPayload live in Interfold.Tags.Contracts (Phase-3 migration).
// PollSocketPayload + PollDeletedSocketPayload live in Interfold.Polls.Contracts (Phase-3 migration).
// FrontSocketPayload + FrontsSocketPayload + FrontIdSocketPayload live in Interfold.Fronting.Contracts (Phase-3 migration).
// GlobalJournalSocketPayload + AlterJournalSocketPayload + EntryDeletedSocketPayload live in Interfold.Journals.Contracts (Phase-3 migration).
// SettingsFieldsUpdatedPayload + SettingsUsernameUpdatedPayload + SettingsSelfUpdatedPayload +
// DiscordAccountLinkedPayload + GoogleAccountLinkedPayload + AppleAccountLinkedPayload +
// ImportCompletedSocketPayload live in Interfold.Settings.Contracts (Phase-3 Settings migration).

// FriendRequestSocketPayload + FriendIdSocketPayload live in Interfold.Friendships.Contracts (Phase-3 migration).

public sealed record SystemIdSocketPayload(SystemId SystemId) : ISocketPayload;
