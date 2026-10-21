using System.Collections.Concurrent;

namespace Interfold.Infrastructure.Coordination;

/// <summary>Named skip / send checkpoints for <c>GET /api/internal/test/fcm-sends</c>.</summary>
public static class FcmSendSkipReasons
{
    public const string NullFcm = "null_fcm";
    public const string NoFriendTokens = "no_friend_tokens";
    public const string MessagingUnavailable = "messaging_unavailable";
    public const string NoVisibleFronts = "no_visible_fronts";
    public const string MulticastFailed = "multicast_failed";
}

public sealed record FcmSendAttempt(
    string SystemId,
    string? FriendSystemId,
    string? SkipReason,
    string? Title,
    string? Body,
    IReadOnlyList<string> AlterIds,
    int TokenCount,
    int SuccessCount,
    int FailureCount,
    IReadOnlyList<string> ErrorCodes,
    DateTimeOffset At);

public interface IFcmSendRecorder
{
    void Record(FcmSendAttempt attempt);

    IReadOnlyList<FcmSendAttempt> Snapshot();
}

public sealed class NullFcmSendRecorder : IFcmSendRecorder
{
    public static readonly NullFcmSendRecorder Instance = new();

    public void Record(FcmSendAttempt attempt)
    {
    }

    public IReadOnlyList<FcmSendAttempt> Snapshot() => [];
}

public sealed class InMemoryFcmSendOutbox : IFcmSendRecorder
{
    private readonly ConcurrentQueue<FcmSendAttempt> _attempts = new();

    public void Record(FcmSendAttempt attempt) => _attempts.Enqueue(attempt);

    public IReadOnlyList<FcmSendAttempt> Snapshot() => _attempts.ToArray();
}
