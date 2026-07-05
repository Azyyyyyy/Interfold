using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;

namespace Interfold.Contracts.Operations;

/// <summary>
/// How a rejected command should be resolved by the caller. Carried on
/// <see cref="ConflictResult.ResolutionHint"/> and surfaced to clients verbatim as the
/// <c>ErrorResponse</c> code (<c>"no_retry"</c> / <c>"manual_merge_required"</c>), so the
/// wire spellings below are frozen.
/// </summary>
[JsonConverter(typeof(LowerCaseEnumJsonConverter<ResolutionHint>))]
public enum ResolutionHint
{
    /// <summary>Duplicate command with a different payload — replaying will never succeed.</summary>
    NoRetry,

    /// <summary>An invariant was violated; the caller must reconcile state before retrying.</summary>
    ManualMergeRequired,
}
