using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>
/// The third-party platform or format an import operation is processing. Persisted as its
/// lowercase wire string in the <c>import_operations.kind</c> column and emitted as
/// the same value in socket completion frames + HTTP dispatch responses.
/// </summary>
/// <remarks>
/// Wire values are locked to <c>"sp"</c> / <c>"pk"</c> / <c>"octocon"</c> via <see cref="JsonStringEnumMemberNameAttribute"/>
/// so the enum names can evolve without breaking the persisted / on-wire representation.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ImportFormat>))]
public enum ImportFormat : short
{
    /// <summary>Simply Plural — handled by <c>SimplyPluralImportService</c>. Wire value: <c>"sp"</c>.</summary>
    [JsonStringEnumMemberName("sp")]
    SimplyPlural = 0,

    /// <summary>PluralKit — handler is currently a stub but inherits the same dispatch model. Wire value: <c>"pk"</c>.</summary>
    [JsonStringEnumMemberName("pk")]
    PluralKit = 1,

    /// <summary>Octocon backup format — handled by <c>OctoconImportService</c>. Wire value: <c>"octocon"</c>.</summary>
    [JsonStringEnumMemberName("octocon")]
    Octocon = 2,
}
