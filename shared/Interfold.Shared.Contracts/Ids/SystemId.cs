using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Ids;

/// <summary>Strongly-typed wrapper around the 7-character alphanumeric system ID on the wire.</summary>
[JsonConverter(typeof(SystemIdJsonConverter))]
public readonly record struct SystemId : IParsable<SystemId>
{
    public string Value { get; }

    public SystemId(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator SystemId(string value) => new(value);

    // Safe implicit widen — SystemId is a public identifier, ToString() returns Value verbatim.
    // Hazard: default(SystemId).Value is null; do not widen a default slot.
    public static implicit operator string(SystemId value) => value.Value;

    /// <summary>
    /// Strips any legacy region prefix (e.g. "nam:abcdefg" -> "abcdefg") from a system ID string.
    /// If no colon is present or the string is empty/whitespace, returns the input as-is.
    /// </summary>
    public static string StripRegionPrefix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var separator = value.IndexOf(':');
        if (separator >= 0 && separator < value.Length - 1)
        {
            return value[(separator + 1)..];
        }

        return value;
    }

    /// <summary>Semantic "same user" test — correct primitive for controller self-request /
    /// self-friendship guards. Canonicalises candidate so raw and same-region-scoped both
    /// self-reject; cross-region candidates compare scoped-to-scoped byte-identical.</summary>
    public bool RepresentsSameUserAs(SystemId candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Value))
        {
            return false;
        }
        
        return string.Equals(StripRegionPrefix(Value), StripRegionPrefix(candidate.Value), StringComparison.Ordinal);
    }
    
    public override string ToString() => Value;

    public static SystemId Parse(string s, IFormatProvider? provider) => new(StripRegionPrefix(s));

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out SystemId result)
    {
        if (s is null)
        {
            result = default;
            return false;
        }

        result = new SystemId(StripRegionPrefix(s));
        return true;
    }
}

internal sealed class SystemIdJsonConverter : StringBackedJsonConverter<SystemId>
{
    protected override SystemId Create(string value) => new(SystemId.StripRegionPrefix(value));
    protected override string GetValue(SystemId value) => value.Value;
}
