using Microsoft.Extensions.Logging;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Infrastructure.Coordination;

/// <summary>No-op <see cref="IFCMService"/> for deployments without a configured FCM
/// credential. Records <see cref="FcmSendSkipReasons.NullFcm"/> so test hooks can
/// distinguish "FCM not wired" from a real send.</summary>
public sealed class NullFCMService(ILogger<NullFCMService> logger, IFcmSendRecorder recorder) : IFCMService
{
    public Task NotifyFrontingChangedAsync(
        SystemId systemId,
        IReadOnlyList<AlterId> currentAlterIds,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "FCM stub: fronting changed for system={SystemId}, alters=[{AlterIds}]",
            systemId, string.Join(",", currentAlterIds));

        recorder.Record(new FcmSendAttempt(
            SystemId: systemId.Value,
            FriendSystemId: null,
            SkipReason: FcmSendSkipReasons.NullFcm,
            Title: null,
            Body: null,
            AlterIds: currentAlterIds.Select(id => id.Value.ToString()).ToArray(),
            TokenCount: 0,
            SuccessCount: 0,
            FailureCount: 0,
            ErrorCodes: [],
            At: DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }
}
