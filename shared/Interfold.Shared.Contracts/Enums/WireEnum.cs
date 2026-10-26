using System.Diagnostics.CodeAnalysis;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>IParsable wrapper so Minimal APIs bind query/route enums through
/// <see cref="EnumWire{TEnum}"/> (wire names + member names) instead of
/// <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/>.</summary>
public readonly record struct WireEnum<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(TEnum Value)
    where TEnum : struct, Enum
{
    public static bool TryParse(string? s, IFormatProvider? provider, out WireEnum<TEnum> result)
    {
        if (EnumWire<TEnum>.TryParse(s, out var parsed))
        {
            result = new WireEnum<TEnum>(parsed);
            return true;
        }

        result = default;
        return false;
    }

    public static implicit operator TEnum(WireEnum<TEnum> value) => value.Value;
}
