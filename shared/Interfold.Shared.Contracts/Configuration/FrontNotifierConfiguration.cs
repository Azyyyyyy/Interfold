namespace Interfold.Shared.Contracts.Configuration;

/// <summary>Debounce knobs for <c>FrontNotifierBackgroundService</c>.
/// Defaults match production (5 s flush, 10 s quiet). Tests set
/// <c>OCTOCON_FRONT_NOTIFIER_QUIET_MS=0</c> and a short flush.</summary>
public sealed class FrontNotifierConfiguration
{
    public const string SectionName = "Octocon:FrontNotifier";

    public const int DefaultQuietMs = 10_000;
    public const int DefaultFlushMs = 5_000;

    /// <summary>Env: OCTOCON_FRONT_NOTIFIER_QUIET_MS. 0 flushes every pending system on the next tick.</summary>
    public int QuietMs { get; set; } = DefaultQuietMs;

    /// <summary>Env: OCTOCON_FRONT_NOTIFIER_FLUSH_MS. Clamped to ≥50 ms.</summary>
    public int FlushMs { get; set; } = DefaultFlushMs;
}
