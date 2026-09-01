using System.Text.Json.Serialization;

namespace Interfold.Bootstrapper.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<BootstrapperReleaseChannel>))]
public enum BootstrapperReleaseChannel
{
    Stable,
    BleedingEdge,
}

internal static class BootstrapperReleaseChannelExtensions
{
    public static string ToWireValue(this BootstrapperReleaseChannel channel) => channel switch
    {
        BootstrapperReleaseChannel.Stable => "stable",
        BootstrapperReleaseChannel.BleedingEdge => "bleeding-edge",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };

    public static BootstrapperReleaseChannel ParseWire(string? wire) => wire?.Trim().ToLowerInvariant() switch
    {
        "stable" or null or "" => BootstrapperReleaseChannel.Stable,
        "bleeding-edge" or "bleedingedge" => BootstrapperReleaseChannel.BleedingEdge,
        _ => throw new InvalidOperationException(
            $"Unknown bootstrapper release channel '{wire}'. Expected 'stable' or 'bleeding-edge'."),
    };

    public static string ToGitHubReleaseTag(this BootstrapperReleaseChannel channel) => channel switch
    {
        BootstrapperReleaseChannel.Stable => "latest",
        BootstrapperReleaseChannel.BleedingEdge => "bleeding-edge",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };
}
