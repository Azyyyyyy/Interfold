using System.Globalization;
using System.Text;
using System.Text.Json;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Settings.Api.Services.Export;

// Reference parity helpers for the export transforms in
// octocon/lib/octocon/accounts.ex.
internal static class ExportHelpers
{
    // Mirrors Elixir's `String.byte_slice(input, 0, maxBytes)`: clip on the UTF-8 byte
    // boundary WITHOUT splitting a codepoint. Null collapses to empty (matches
    // `(input || "") |> String.byte_slice`).
    public static string ByteTruncateUtf8(string? input, int maxBytes)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var utf8 = Encoding.UTF8;
        var byteCount = utf8.GetByteCount(input);
        if (byteCount <= maxBytes)
            return input;

        var bytes = utf8.GetBytes(input);
        var take = maxBytes;
        // Walk back until we're not sitting on a UTF-8 continuation byte (10xxxxxx) —
        // otherwise GetString would either throw or emit a replacement character.
        while (take > 0 && (bytes[take] & 0b1100_0000) == 0b1000_0000)
        {
            take--;
        }
        return utf8.GetString(bytes, 0, take);
    }

    // Mirrors Elixir's `format_import_color/1` (accounts.ex:1417): nil → nil, "#RRGGBB" → "RRGGBB",
    // anything else (short forms, alpha suffixes, missing '#') → nil so PK ignores the field.
    public static string? FormatImportColor(HexColor? color)
    {
        if (color is not { } value)
            return null;

        var raw = value.Value;
        // `byte_size(color) == 7` ⇒ "#" + 6 hex chars; hex validity is already enforced by
        // HexColor's ctor so a length check is sufficient here.
        if (raw.Length != 7 || raw[0] != '#')
            return null;

        return raw[1..];
    }

    // Split on the literal "text" sentinel (Elixir's `String.split(proxy |> trim, "text", parts: 2)`).
    // If "text" doesn't appear, the reference errors on the pattern-match; guard here so a bad
    // row can't 500 the entire export.
    public static PkProxyTag? TrySplitProxyTag(string proxy)
    {
        var trimmed = proxy.Trim();
        var idx = trimmed.IndexOf("text", StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var prefix = trimmed[..idx];
        var suffix = trimmed[(idx + "text".Length)..];
        return new PkProxyTag(
            ByteTruncateUtf8(prefix, 50),
            ByteTruncateUtf8(suffix, 50));
    }

    // Formats UTC ISO-8601 without trailing Z or offset (matches Elixir's NaiveDateTime output
    // e.g. "2024-01-02T03:04:05").
    public static string FormatExportTimestamp(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string FormatExportTimestamp(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    // Maps custom field type to Octocon export schema ("text" | "number" | "boolean"),
    // falling back to "text" for unsupported/extended types.
    public static string MapExportFieldType(FieldType type) => type switch
    {
        FieldType.Text => "text",
        FieldType.Number => "number",
        FieldType.Boolean => "boolean",
        _ => "text",
    };

    // Adapts poll data interior for the Octocon export schema ("choice"), while the
    // rest of the system and Kotlin client consume "choices".
    public static JsonElement FormatPollDataForExport(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return data;

        if (!data.TryGetProperty("choices", out _))
            return data;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in data.EnumerateObject())
            {
                if (prop.NameEquals("choices"))
                {
                    writer.WritePropertyName("choice");
                    prop.Value.WriteTo(writer);
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }
}
