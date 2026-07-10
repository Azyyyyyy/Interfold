using System.Collections.Concurrent;
using System.Net.WebSockets;
using Interfold.Contracts.Ids;
using Microsoft.Extensions.Logging;

namespace Interfold.Api.Socket;

public sealed class SocketPushContext
{
    public SocketPushContext(
        WebSocket socket,
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
    /// The scoped composite form of the socket's principal, parsed from the JWT sub
    /// inside <c>WebSocketHandler.IsSocketJoinTokenAuthorizedAsync</c> (Round-2 Commit
    /// 13, canvas #24). Fed to
    /// <see cref="Interfold.Domain.Abstractions.IClusterEventBus.SubscribeAsync{TEvent}"/>
    /// by <see cref="SocketEventPumpRunner.RunAllAsync"/> so the pump's ~38 subscriptions
    /// filter on the scoped composite rather than the raw topic id. Pre-R2C13 the pump
    /// passed a raw <c>SystemId</c> alongside this and the bus normalised both sides
    /// through <c>SystemIdNormalization.StripRegionPrefix</c> on every publish tick —
    /// the asymmetry was documented but a real bug vector if a subscriber ever supplied
    /// a scoped-shaped SystemId without stripping.
    ///
    /// <para>
    /// Post-Round-5 dead-code sweep: the sibling <c>JoinedSystemId</c> (raw
    /// <see cref="SystemId"/>) that this doc used to cross-reference has been deleted —
    /// grep-verified zero live readers, the socket event handlers all route on
    /// <c>evt.TargetSystemId</c> fed into <see cref="TryGetSystemTopic"/>, never on the
    /// context's own bound id. This property is now the only "who is this socket bound
    /// to" surface on the context.
    /// </para>
    ///
    /// <para>
    /// Nullable because it's <see langword="null"/> for anonymous sockets that never
    /// completed a system-topic join. Every downstream consumer already handles the
    /// null case (the bus's target filter treats null as "no filter").
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
