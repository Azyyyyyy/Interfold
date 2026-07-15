using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// Strongly-typed wrapper around the per-system alter id. The underlying type is
/// <see cref="short"/> — matching every canonical Scylla column that stores it
/// (<c>alters.id</c>, <c>tags.alter_id</c>, <c>alter_journals.alter_id</c>,
/// <c>alter_journals_by_alter.alter_id</c>, <c>fronts.alter_id</c>,
/// <c>users.primary_front_smallint</c>, and the <c>current_fronts</c> /
/// <c>fronts_by_alter</c> lookup denormalisations, all declared as <c>smallint</c>). The
/// wire form is still a JSON number; <see cref="short"/> values fit trivially in an
/// Int32 JSON number so emission is byte-identical to the pre-narrowing shape.
///
/// <para>
/// <b>Range enforcement moves earlier.</b> Before this narrowing an out-of-<c>short</c>
/// value could reach a handler and only get rejected by
/// <see cref="Validation.ValidAlterIdAttribute"/>. Now the JSON reader (<c>GetInt16</c>)
/// throws on ingest, ASP.NET Core maps that to a 400, and no code path downstream ever
/// sees a value outside <c>[short.MinValue, short.MaxValue]</c>.
/// </para>
///
/// <para>See <see cref="SystemId"/> for the wider typed-id-rollout story.</para>
/// </summary>
[JsonConverter(typeof(AlterIdJsonConverter))]
public readonly record struct AlterId : IParsable<AlterId>
{
    public short Value { get; }

    public AlterId(short value)
    {
        Value = value;
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(AlterId)v</c> instead of
    /// <c>new AlterId(v)</c>. See <see cref="SystemId"/> for the wider rationale.
    /// </summary>
    public static explicit operator AlterId(short value) => new(value);

    /// <summary>
    /// Implicit widen to <see cref="int"/>. The underlying value is a <see cref="short"/>,
    /// so this is a lossless widen; downstream sites that already declared <c>int</c>
    /// (LINQ ordering, JSON emission through Utf8JsonWriter.WriteNumberValue) keep
    /// compiling. Prefer <see cref="ToStorageShort"/> at CQL bind sites — every alter-id
    /// column in the schema is <c>smallint</c>, so binding a <see cref="short"/> is the
    /// only shape the driver will accept.
    /// </summary>
    public static implicit operator int(AlterId value) => value.Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The Cassandra storage form — every canonical alter-id column is <c>smallint</c>.
    /// A byte-identical accessor now that <see cref="Value"/> is a <see cref="short"/>,
    /// kept as a named method so CQL bind sites read as "explicit storage shape" rather
    /// than a bare property access.
    /// </summary>
    public short ToStorageShort() => Value;

    /// <summary>Rehydrates from a <c>smallint</c> column read.</summary>
    public static AlterId FromStorageShort(short value) => new(value);

    /// <summary>
    /// Null-preserving rehydration from a nullable <c>smallint</c> column read. Repository
    /// code uses this at every read site that maps <c>users.primary_front_smallint</c>
    /// (nullable) onto an <see cref="AlterId"/>, so the null-check + <c>new AlterId(v)</c>
    /// pattern lives on the type instead of scattering through the repositories.
    /// </summary>
    public static AlterId? FromNullableStorageShort(short? value) =>
        value is { } v ? new AlterId(v) : null;

    public static AlterId Parse(string s, IFormatProvider? provider)
        => new(short.Parse(s, System.Globalization.NumberStyles.Integer, provider ?? System.Globalization.CultureInfo.InvariantCulture));

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out AlterId result)
    {
        if (short.TryParse(s, System.Globalization.NumberStyles.Integer, provider ?? System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            result = new AlterId(value);
            return true;
        }

        result = default;
        return false;
    }
}

internal sealed class AlterIdJsonConverter : JsonConverter<AlterId>
{
    public override AlterId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetInt16());

    public override void Write(Utf8JsonWriter writer, AlterId value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);
}
