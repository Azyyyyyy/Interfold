using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>
/// Rolling channel (<c>stable</c> / <c>bleeding-edge</c>) or an immutable pin tag
/// (<c>bootstrap-vX.Y.Z</c>).
/// </summary>
[JsonConverter(typeof(BootstrapperReleaseChannelJsonConverter))]
public readonly struct BootstrapperReleaseChannel : IEquatable<BootstrapperReleaseChannel>
{
    public const string PinTagPrefix = "bootstrap-v";

    public static BootstrapperReleaseChannel Stable { get; } = new("stable");
    public static BootstrapperReleaseChannel BleedingEdge { get; } = new("bleeding-edge");

    private readonly string _wire;

    private BootstrapperReleaseChannel(string wire) => _wire = wire;

    private string Wire => _wire ?? "stable";

    public bool IsPinned => IsPinTag(Wire);

    public string ToWireValue() => Wire;

    /// <summary>
    /// Mirror-folder / pin tag for direct download URLs.
    /// Live rolling channels publish unique <c>stable-*</c> / <c>bleeding-edge-*</c> tags and
    /// resolve via the Releases API — these wire names remain the local fake-mirror folders.
    /// </summary>
    public string ToGitHubReleaseTag() => IsPinned
        ? Wire
        : Wire switch
        {
            "stable" => "latest",
            "bleeding-edge" => "bleeding-edge",
            _ => throw new InvalidOperationException($"Unknown bootstrapper release channel '{Wire}'."),
        };

    public static BootstrapperReleaseChannel ParseWire(string? wire)
    {
        var trimmed = wire?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Stable;
        }

        var lower = trimmed.ToLowerInvariant();
        return lower switch
        {
            "stable" => Stable,
            "bleeding-edge" or "bleedingedge" => BleedingEdge,
            _ when IsPinTag(lower) => new BootstrapperReleaseChannel(lower),
            _ => throw new InvalidOperationException(
                $"Unknown bootstrapper release channel '{wire}'. Expected 'stable', 'bleeding-edge', or a pin tag like 'bootstrap-v0.0.1'."),
        };
    }

    /// <summary>
    /// Pin tags are <c>bootstrap-v</c> + digit-led semver-ish
    /// (<c>bootstrap-v0.0.1</c>, <c>bootstrap-v1.2.3-rc.1</c>).
    /// </summary>
    internal static bool IsPinTag(string wire)
    {
        if (!wire.StartsWith(PinTagPrefix, StringComparison.Ordinal)
            || wire.Length <= PinTagPrefix.Length
            || !char.IsAsciiDigit(wire[PinTagPrefix.Length]))
        {
            return false;
        }

        for (var i = PinTagPrefix.Length + 1; i < wire.Length; i++)
        {
            var c = wire[i];
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '_')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    public bool Equals(BootstrapperReleaseChannel other)
        => string.Equals(Wire, other.Wire, StringComparison.Ordinal);

    public override bool Equals(object? obj)
        => obj is BootstrapperReleaseChannel other && Equals(other);

    public override int GetHashCode()
        => StringComparer.Ordinal.GetHashCode(Wire);

    public static bool operator ==(BootstrapperReleaseChannel left, BootstrapperReleaseChannel right)
        => left.Equals(right);

    public static bool operator !=(BootstrapperReleaseChannel left, BootstrapperReleaseChannel right)
        => !left.Equals(right);

    public override string ToString() => Wire;
}

internal sealed class BootstrapperReleaseChannelJsonConverter : JsonConverter<BootstrapperReleaseChannel>
{
    public override BootstrapperReleaseChannel Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return BootstrapperReleaseChannel.Stable;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected string for bootstrapper release channel, got {reader.TokenType}.");
        }

        return BootstrapperReleaseChannel.ParseWire(reader.GetString());
    }

    public override void Write(
        Utf8JsonWriter writer, BootstrapperReleaseChannel value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToWireValue());
}
