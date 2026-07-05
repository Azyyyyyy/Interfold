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
    /// The system id this socket is bound to (captured from the successful <c>phx_join</c>).
    /// Used by <c>SocketEventPumpRunner</c> as the <c>targetSystemId</c> for every bus
    /// subscription so the pump only sees events whose
    /// <c>ITargetedClusterEvent.TargetSystemId</c> matches this socket.
    /// </summary>
    public SystemId JoinedSystemId { get; }
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
