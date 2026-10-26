using System.Diagnostics.CodeAnalysis;

namespace Interfold.Shared.Contracts.Enums;

// Parse-side helpers for env-driven enums: blank → compiled-in default, unknown → throw.
// Tolerant "unknown → null" callers invoke EnumWire<T>.TryParse directly.
public static class EnumWireExtensions
{
    public static string ToWire<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(this TEnum value)
        where TEnum : struct, Enum
        => EnumWire<TEnum>.ToWire(value);

    public static bool TryParseWire<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(
        this string? raw, out TEnum value)
        where TEnum : struct, Enum
        => EnumWire<TEnum>.TryParse(raw, out value);

    // Blank → default, unknown-non-empty → throw. Shared shape for env-parsers below.
    public static T ParseWithDefault<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(
        string? raw, T defaultValue, Func<string, string> errorProvider)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (raw.TryParseWire<T>(out var value))
        {
            return value;
        }

        throw new InvalidOperationException(errorProvider(raw.Trim()));
    }

    public static PersistenceMode ParsePersistenceMode(string? raw)
        => ParseWithDefault(raw, PersistenceMode.Sqlite, trimmed => $"Unsupported OCTOCON_PERSISTENCE value '{trimmed}'. Expected: sqlite | inmemory.");
}
