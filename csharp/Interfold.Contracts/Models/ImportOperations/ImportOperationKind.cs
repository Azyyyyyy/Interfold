using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;

namespace Interfold.Contracts.Models.ImportOperations;

/// <summary>
/// The third-party platform an import operation is pulling data from. Persisted as its
/// lowercase enum-name string in the <c>import_operations.kind</c> column and emitted as
/// the same value in socket completion frames + HTTP dispatch responses. A future
/// integration adds a value here (and a matching socket-frame constant in
/// <c>SocketEventNames.Imports</c>).
/// </summary>
/// <remarks>
/// Wire values are locked to <c>"sp"</c> / <c>"pk"</c> via a bespoke <see cref="JsonConverter"/>
/// so the enum names can evolve without breaking the persisted / on-wire representation.
/// </remarks>
[JsonConverter(typeof(ImportOperationKindJsonConverter))]
public enum ImportOperationKind
{
    /// <summary>Simply Plural — handled by <c>SimplyPluralImportService</c>. Wire value: <c>"sp"</c>.</summary>
    SimplyPlural,

    /// <summary>PluralKit — handler is currently a stub but inherits the same dispatch model. Wire value: <c>"pk"</c>.</summary>
    PluralKit,
}

/// <summary>
/// Wire values that the enum members are persisted / emitted as. Also used as the
/// convenient stable string form outside JSON (repositories, log lines).
/// </summary>
public static class ImportOperationKindExtensions
{
    public const string SimplyPluralWire = "sp";
    public const string PluralKitWire = "pk";

    public static string ToWireValue(this ImportOperationKind kind) => kind switch
    {
        ImportOperationKind.SimplyPlural => SimplyPluralWire,
        ImportOperationKind.PluralKit => PluralKitWire,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            "Unhandled ImportOperationKind — add the wire mapping when introducing a new value."),
    };

    public static ImportOperationKind ParseWireValue(string value) => value switch
    {
        SimplyPluralWire => ImportOperationKind.SimplyPlural,
        PluralKitWire => ImportOperationKind.PluralKit,
        _ => throw new ArgumentException(
            $"'{value}' is not a valid ImportOperationKind wire value. Expected '{SimplyPluralWire}' or '{PluralKitWire}'.",
            nameof(value)),
    };

    public static bool TryParseWireValue(string? value, out ImportOperationKind kind)
    {
        switch (value)
        {
            case SimplyPluralWire:
                kind = ImportOperationKind.SimplyPlural;
                return true;
            case PluralKitWire:
                kind = ImportOperationKind.PluralKit;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}

/// <summary>
/// JSON converter that keeps <see cref="ImportOperationKind"/> serialising as its short
/// wire value (<c>"sp"</c>/<c>"pk"</c>) rather than the PascalCase enum name, so command
/// results, socket frames, and persisted <c>import_operations.kind</c> rows all stay
/// byte-identical to the pre-enum representation.
/// </summary>
internal sealed class ImportOperationKindJsonConverter : JsonConverter<ImportOperationKind>
{
    public override ImportOperationKind Read(
        ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (value is null)
        {
            throw new System.Text.Json.JsonException(
                $"Expected string for {nameof(ImportOperationKind)}, got null.");
        }

        return ImportOperationKindExtensions.ParseWireValue(value);
    }

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        ImportOperationKind value,
        System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToWireValue());
    }
}
