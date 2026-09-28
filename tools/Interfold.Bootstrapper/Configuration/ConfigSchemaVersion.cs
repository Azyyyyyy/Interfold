using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>
/// Bootstrap config schema version. Additive field additions bump the minor
/// (2.1, 2.2) instead of jumping the major. The JSON file stores an integer
/// with no decimal: legacy <c>1</c>/<c>2</c> mean 1.0/2.0; later values are
/// <c>major * 10 + minor</c> (2.1 → <c>21</c>).
/// </summary>
[JsonConverter(typeof(ConfigSchemaVersionJsonConverter))]
public readonly record struct ConfigSchemaVersion : IComparable<ConfigSchemaVersion>
{
    public static readonly ConfigSchemaVersion V1 = new(1, 0);
    public static readonly ConfigSchemaVersion V2 = new(2, 0);

    public int Major { get; }
    public int Minor { get; }

    public ConfigSchemaVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(major, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(major, 9);
        ArgumentOutOfRangeException.ThrowIfLessThan(minor, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minor, 9);
        Major = major;
        Minor = minor;
    }

    public int ToWire() => Minor == 0 ? Major : Major * 10 + Minor;

    public static ConfigSchemaVersion FromWire(int wire)
    {
        if (wire < 10)
        {
            return new(wire, 0);
        }

        return new(wire / 10, wire % 10);
    }

    public static ConfigSchemaVersion Parse(decimal value)
    {
        var major = (int)decimal.Truncate(value);
        var frac = value - major;
        if (frac == 0)
        {
            return FromWire(major);
        }

        var minor = (int)decimal.Round(frac * 10m, 0, MidpointRounding.AwayFromZero);
        return new(major, minor);
    }

    public int CompareTo(ConfigSchemaVersion other)
    {
        var major = Major.CompareTo(other.Major);
        return major != 0 ? major : Minor.CompareTo(other.Minor);
    }

    public static bool operator <(ConfigSchemaVersion left, ConfigSchemaVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ConfigSchemaVersion left, ConfigSchemaVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ConfigSchemaVersion left, ConfigSchemaVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ConfigSchemaVersion left, ConfigSchemaVersion right) => left.CompareTo(right) >= 0;

    public override string ToString()
        => Minor == 0
            ? Major.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}

internal sealed class ConfigSchemaVersionJsonConverter : JsonConverter<ConfigSchemaVersion>
{
    public override ConfigSchemaVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out var wire))
            {
                return ConfigSchemaVersion.FromWire(wire);
            }

            if (reader.TryGetDecimal(out var dec))
            {
                return ConfigSchemaVersion.Parse(dec);
            }
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wire))
            {
                return ConfigSchemaVersion.FromWire(wire);
            }

            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec))
            {
                return ConfigSchemaVersion.Parse(dec);
            }
        }

        throw new JsonException("schemaVersion must be a number.");
    }

    public override void Write(Utf8JsonWriter writer, ConfigSchemaVersion value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.ToWire());
}
