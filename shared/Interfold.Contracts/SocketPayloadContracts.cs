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

public sealed record SocketJoinInitPayload(
    SocketSelfReadModel System,
    IReadOnlyList<AlterReadModel> Alters,
    IReadOnlyList<FrontActiveReadModel> Fronts,
    IReadOnlyList<TagReadModel> Tags) : ISocketPayload;

public sealed record SocketJoinReconnectPayload(SocketSelfReadModel System) : ISocketPayload;

public sealed record SocketJoinBatchedPayload(
    bool Batched,
    SocketSelfReadModel System,
    IReadOnlyList<AlterReadModel>? Alters,
    IReadOnlyList<FrontActiveReadModel>? Fronts,
    IReadOnlyList<TagReadModel>? Tags) : ISocketPayload;

public sealed record SocketBatchedAltersPayload(int BatchIndex, int TotalBatches, IReadOnlyList<AlterReadModel> Alters) : ISocketPayload;

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

public sealed record AlterSocketPayload(AlterReadModel Alter) : ISocketPayload;

public sealed record AlterDeletedSocketPayload(AlterId AlterId) : ISocketPayload;

// TagSocketPayload + TagDeletedSocketPayload live in Interfold.Tags.Contracts (Phase-3 migration).
// PollSocketPayload + PollDeletedSocketPayload live in Interfold.Polls.Contracts (Phase-3 migration).
// FrontSocketPayload + FrontsSocketPayload + FrontIdSocketPayload live in Interfold.Fronting.Contracts (Phase-3 migration).

public sealed record AlterIdSocketPayload(AlterId? AlterId) : ISocketPayload;

public sealed record GlobalJournalSocketPayload(JournalReadModel Entry) : ISocketPayload;

public sealed record AlterJournalSocketPayload(AlterJournalReadModel Entry) : ISocketPayload;

public sealed record EntryDeletedSocketPayload(EntryId EntryId) : ISocketPayload;

public sealed record SettingsFieldsUpdatedPayload(IReadOnlyList<SettingsFieldReadModel> Fields) : ISocketPayload;

public sealed record SettingsUsernameUpdatedPayload(Username Username) : ISocketPayload;

public sealed record SettingsSelfUpdatedPayload(SocketSelfReadModel Data) : ISocketPayload;

public sealed record DiscordAccountLinkedPayload(DiscordId DiscordId) : ISocketPayload;

public sealed record GoogleAccountLinkedPayload(Email Email) : ISocketPayload;

public sealed record AppleAccountLinkedPayload(AppleId AppleId) : ISocketPayload;

public sealed record FriendRequestSocketPayload(FriendshipRequestModel Request, FriendProfileReadModel System) : ISocketPayload;

public sealed record FriendIdSocketPayload(SystemId FriendId) : ISocketPayload;

public sealed record SystemIdSocketPayload(SystemId SystemId) : ISocketPayload;

public sealed record ImportCompletedSocketPayload(int AlterCount) : ISocketPayload;
