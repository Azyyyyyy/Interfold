using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

// The remaining string-backed IDs share the same shape as SystemId. Rather than duplicating
// the boilerplate, we generate one file per ID so future changes (extra invariants, culture-
// specific formatting, etc.) are locally overridable per-ID. Each ID:
//   - is a readonly record struct backed by string
//   - implements IParsable<T> for [FromRoute] binding
//   - carries a wire-identical JsonConverter emitting the raw underlying value

/// <summary>Strongly-typed wrapper around a tag id (32-char hex UUID). See <see cref="SystemId"/> for the migration story.</summary>
[JsonConverter(typeof(TagIdJsonConverter))]
public readonly record struct TagId(string Value) : IParsable<TagId>
{
    public override string ToString() => Value;
    public static TagId Parse(string s, IFormatProvider? provider) => new(s);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out TagId result)
    {
        if (s is null) { result = default; return false; }
        result = new TagId(s);
        return true;
    }
}

/// <summary>Strongly-typed wrapper around a poll id (32-char hex UUID).</summary>
[JsonConverter(typeof(PollIdJsonConverter))]
public readonly record struct PollId(string Value) : IParsable<PollId>
{
    public override string ToString() => Value;
    public static PollId Parse(string s, IFormatProvider? provider) => new(s);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out PollId result)
    {
        if (s is null) { result = default; return false; }
        result = new PollId(s);
        return true;
    }
}

/// <summary>Strongly-typed wrapper around a journal entry id.</summary>
[JsonConverter(typeof(EntryIdJsonConverter))]
public readonly record struct EntryId(string Value) : IParsable<EntryId>
{
    public override string ToString() => Value;
    public static EntryId Parse(string s, IFormatProvider? provider) => new(s);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out EntryId result)
    {
        if (s is null) { result = default; return false; }
        result = new EntryId(s);
        return true;
    }
}

/// <summary>Strongly-typed wrapper around a front id (TimeUuid-backed).</summary>
[JsonConverter(typeof(FrontIdJsonConverter))]
public readonly record struct FrontId(string Value) : IParsable<FrontId>
{
    public override string ToString() => Value;
    public static FrontId Parse(string s, IFormatProvider? provider) => new(s);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out FrontId result)
    {
        if (s is null) { result = default; return false; }
        result = new FrontId(s);
        return true;
    }
}

/// <summary>Strongly-typed wrapper around a settings-field id (32-char hex UUID).</summary>
[JsonConverter(typeof(FieldIdJsonConverter))]
public readonly record struct FieldId(string Value) : IParsable<FieldId>
{
    public override string ToString() => Value;
    public static FieldId Parse(string s, IFormatProvider? provider) => new(s);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out FieldId result)
    {
        if (s is null) { result = default; return false; }
        result = new FieldId(s);
        return true;
    }
}

internal sealed class TagIdJsonConverter : JsonConverter<TagId>
{
    public override TagId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, TagId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

internal sealed class PollIdJsonConverter : JsonConverter<PollId>
{
    public override PollId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, PollId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

internal sealed class EntryIdJsonConverter : JsonConverter<EntryId>
{
    public override EntryId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, EntryId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

internal sealed class FrontIdJsonConverter : JsonConverter<FrontId>
{
    public override FrontId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, FrontId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

internal sealed class FieldIdJsonConverter : JsonConverter<FieldId>
{
    public override FieldId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, FieldId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
