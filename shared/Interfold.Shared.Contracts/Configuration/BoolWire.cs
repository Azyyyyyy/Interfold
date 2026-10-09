namespace Interfold.Shared.Contracts.Configuration;

/// <summary>Wire spellings for Aspire boolean parameter toggles.</summary>
public static class BoolWire
{
    public const string TrueValue = "true";
    public const string FalseValue = "false";

    public static string ToWireValue(bool value) => value ? TrueValue : FalseValue;

    /// <summary>Historical toggle semantics: null/empty → fallback, else "false" (case-insensitive) → off, everything else → on.</summary>
    public static bool ParseToggle(string? raw, bool fallback)
        => string.IsNullOrEmpty(raw)
            ? fallback
            : !string.Equals(raw, FalseValue, StringComparison.OrdinalIgnoreCase);
}
