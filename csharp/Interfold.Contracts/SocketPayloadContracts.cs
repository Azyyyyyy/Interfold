using System.Text.Json;
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

public sealed record SocketReasonResponse(string Reason) : ISocketPayload;

public sealed record SocketEndpointProxyResponse(int Status, string Body) : ISocketPayload;

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

public sealed record SocketBatchedTagsPayload(int BatchIndex, int TotalBatches, IReadOnlyList<TagReadModel> Tags) : ISocketPayload;

public sealed record SocketBatchedFrontsPayload(int BatchIndex, int TotalBatches, IReadOnlyList<FrontActiveReadModel> Fronts) : ISocketPayload;

public sealed record SocketSelfReadModel(
    SystemId Id,
    string? Username,
    string? Description,
    string? AvatarUrl,
    AvatarSource? AvatarSource,
    string? DiscordId,
    string? GoogleId,
    string? AppleId,
    string? Email,
    AutoproxyMode AutoproxyMode,
    bool ShowSystemTag,
    int LifetimeAlterCount,
    AlterId? PrimaryFront,
    IReadOnlyList<SettingsFieldReadModel> Fields,
    bool EncryptionInitialized) : ISocketPayload;

public sealed record AlterSocketPayload(AlterReadModel Alter) : ISocketPayload;

public sealed record AlterDeletedSocketPayload(AlterId AlterId) : ISocketPayload;

public sealed record TagSocketPayload(TagReadModel Tag) : ISocketPayload;

public sealed record TagDeletedSocketPayload(TagId TagId) : ISocketPayload;

public sealed record PollSocketPayload(PollReadModel Poll) : ISocketPayload;

public sealed record PollDeletedSocketPayload(PollId PollId) : ISocketPayload;

public sealed record FrontSocketPayload(FrontActiveReadModel Front) : ISocketPayload;

public sealed record FrontsSocketPayload(IReadOnlyList<FrontActiveReadModel> Fronts) : ISocketPayload;

public sealed record AlterIdSocketPayload(AlterId? AlterId) : ISocketPayload;

public sealed record FrontIdSocketPayload(FrontId FrontId) : ISocketPayload;

public sealed record GlobalJournalSocketPayload(JournalReadModel Entry) : ISocketPayload;

public sealed record AlterJournalSocketPayload(AlterJournalReadModel Entry) : ISocketPayload;

public sealed record EntryDeletedSocketPayload(EntryId EntryId) : ISocketPayload;

public sealed record SettingsFieldsUpdatedPayload(IReadOnlyList<SettingsFieldReadModel> Fields) : ISocketPayload;

public sealed record SettingsUsernameUpdatedPayload(string Username) : ISocketPayload;

public sealed record SettingsSelfUpdatedPayload(SocketSelfReadModel Data) : ISocketPayload;

public sealed record DiscordAccountLinkedPayload(string DiscordId) : ISocketPayload;

public sealed record GoogleAccountLinkedPayload(string Email) : ISocketPayload;

public sealed record AppleAccountLinkedPayload(string AppleId) : ISocketPayload;

public sealed record FriendRequestSocketPayload(FriendshipRequestModel Request, FriendProfileReadModel System) : ISocketPayload;

public sealed record FriendIdSocketPayload(SystemId FriendId) : ISocketPayload;

public sealed record SystemIdSocketPayload(SystemId SystemId) : ISocketPayload;

public sealed record ImportCompletedSocketPayload(int AlterCount) : ISocketPayload;
