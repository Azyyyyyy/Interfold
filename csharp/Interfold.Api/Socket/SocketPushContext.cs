using System.Collections.Concurrent;
using System.Net.WebSockets;
using Interfold.Contracts.Ids;
using Microsoft.Extensions.Logging;

namespace Interfold.Api.Socket;

public sealed class SocketPushContext
{
    public SocketPushContext(
        WebSocket socket,
        SystemId joinedSystemId,
        ScopedSystemId? joinedScopedSystemId,
        ConcurrentDictionary<string, byte> joinedTopics,
        ConcurrentDictionary<string, string?> topicJoinReference,
        ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken,
        string? requestOrigin = null,
        ILogger? logger = null)
    {
        Socket = socket;
        JoinedSystemId = joinedSystemId;
        JoinedScopedSystemId = joinedScopedSystemId;
        JoinedTopics = joinedTopics;
        TopicJoinReference = topicJoinReference;
        TopicReplyAsArrayFrame = topicReplyAsArrayFrame;
        SendGate = sendGate;
        CancellationToken = cancellationToken;
        RequestOrigin = requestOrigin;
        Logger = logger;
    }

    public WebSocket Socket { get; }
    /// <summary>
    /// The raw system id this socket is bound to (captured from the successful
    /// <c>phx_join</c>'s topic string, which is on the wire in raw
    /// <c>system:{rawId}</c> form). Preserved as the pre-Slice-4 name / type so the
    /// socket event handlers (which route by <c>TryGetSystemTopic</c>) don't have to
    /// change signatures.
    /// </summary>
    public SystemId JoinedSystemId { get; }

    /// <summary>
    /// Round-2 Commit 13 (canvas #24): the scoped composite form of the socket's
    /// principal, parsed from the JWT sub inside
    /// <c>WebSocketHandler.IsSocketJoinTokenAuthorizedAsync</c>. Fed to
    /// <see cref="Interfold.Domain.Abstractions.IClusterEventBus.SubscribeAsync{TEvent}"/>
    /// by <see cref="SocketEventPumpRunner.RunAllAsync"/> so the pump's ~38 subscriptions
    /// filter on the scoped composite rather than the raw topic id. Pre-Round-2 the pump
    /// passed <see cref="JoinedSystemId"/> (raw) and the bus normalised both sides
    /// through <c>SystemIdNormalization.StripRegionPrefix</c> on every publish tick —
    /// the asymmetry was documented but a real bug vector if a subscriber ever supplied
    /// a scoped-shaped SystemId without stripping.
    ///
    /// <para>
    /// Nullable because it's <see langword="null"/> for anonymous sockets that never
    /// completed a system-topic join (the DTO carries <see cref="JoinedSystemId"/> which
    /// defaults to <c>default(SystemId)</c> in that case, but there's no matching scoped
    /// composite to synthesise without a region resolver).
    /// </para>
    /// </summary>
    public ScopedSystemId? JoinedScopedSystemId { get; }
    public ConcurrentDictionary<string, byte> JoinedTopics { get; }
    public ConcurrentDictionary<string, string?> TopicJoinReference { get; }
    public ConcurrentDictionary<string, bool> TopicReplyAsArrayFrame { get; }
    public SemaphoreSlim SendGate { get; }
    public CancellationToken CancellationToken { get; }
    /// <summary>
    /// The origin of the HTTP request that upgraded to this WebSocket
    /// (e.g. <c>https://api.example.com</c>). Used to qualify relative avatar URLs.
    /// </summary>
    public string? RequestOrigin { get; }

    public ILogger? Logger { get; }

    public bool TryGetSystemTopic(SystemId systemId, out string topic, out string? joinRef, out bool asArray)
    {
        topic = new SystemTopic(systemId).ToWireString();
        if (!JoinedTopics.ContainsKey(topic))
        {
            var matchedTopic = JoinedTopics.Keys.FirstOrDefault(t =>
                SystemTopic.TryParse(t, out var joined)
                && SystemTopic.IdMatches(joined.Id.Value, systemId.Value));

            if (matchedTopic is null)
            {
                joinRef = null;
                asArray = false;
                return false;
            }

            topic = matchedTopic;
        }

        TopicJoinReference.TryGetValue(topic, out joinRef);
        TopicReplyAsArrayFrame.TryGetValue(topic, out asArray);
        return true;
    }

    public Task SendAsync<TPayload>(string topic, string? joinRef, bool asArray, string eventName, TPayload payload)
        => WebSocketEvents.SendPhoenixPushAsync(
            Socket,
            topic,
            joinRef,
            eventName,
            payload,
            asArray,
            CancellationToken,
            SendGate);
}
