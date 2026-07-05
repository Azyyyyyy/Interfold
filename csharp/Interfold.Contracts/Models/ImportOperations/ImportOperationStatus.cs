namespace Interfold.Contracts.Models.ImportOperations;

/// <summary>
/// Lifecycle of an asynchronous third-party import (SP or PK). Persisted via
/// <see cref="ImportOperationStatusExtensions.ToWireValue"/> in the
/// <c>import_operations.status</c> column (today identical to the enum member name —
/// the explicit helper freezes the DB spelling against member renames) and read back
/// with <see cref="ImportOperationStatusExtensions.TryParseWireValue"/>.
/// </summary>
public enum ImportOperationStatus
{
    /// <summary>
    /// The operation row exists and a worker has been signalled to start, but execution
    /// has not yet begun. This is the state the row is created in by
    /// <c>IImportOperationRepository.TryClaimAsync</c>. A row should not stay in this
    /// state for long; if it does, the worker has not picked it up (host bottleneck) or
    /// the worker process crashed before transitioning to <see cref="Running"/>.
    /// </summary>
    Queued,

    /// <summary>
    /// The background worker has begun executing the import. Transitioned via
    /// <c>MarkRunningAsync</c>. Stale rows in this state are recovered by the
    /// background service's startup sweep (any <c>Running</c> row whose
    /// <c>started_at</c> is older than the configured ceiling is rewritten to
    /// <see cref="Failed"/> with a host-restart error code).
    /// </summary>
    Running,

    /// <summary>
    /// Terminal: the importer returned <c>Success = true</c>. <c>alter_count</c> is
    /// populated. The completion event has been published on the cluster bus.
    /// </summary>
    Succeeded,

    /// <summary>
    /// Terminal: the importer either returned <c>Success = false</c> (graceful failure
    /// like auth or encryption mismatch) or threw. <c>error_code</c> and
    /// <c>error_message</c> are populated. The failure event has been published on the
    /// cluster bus.
    /// </summary>
    Failed,
}

public static class ImportOperationStatusExtensions
{
    /// <summary>The DB-frozen <c>import_operations.status</c> spelling (the enum member name).</summary>
    public static string ToWireValue(this ImportOperationStatus status) => status switch
    {
        ImportOperationStatus.Queued => nameof(ImportOperationStatus.Queued),
        ImportOperationStatus.Running => nameof(ImportOperationStatus.Running),
        ImportOperationStatus.Succeeded => nameof(ImportOperationStatus.Succeeded),
        ImportOperationStatus.Failed => nameof(ImportOperationStatus.Failed),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled ImportOperationStatus."),
    };

    /// <summary>
    /// Tolerant reverse mapping for DB reads (case-insensitive, matching the historical
    /// <c>Enum.TryParse</c> behavior). Unknown spellings return false.
    /// </summary>
    public static bool TryParseWireValue(string? raw, out ImportOperationStatus status)
    {
        switch (raw?.ToLowerInvariant())
        {
            case "queued": status = ImportOperationStatus.Queued; return true;
            case "running": status = ImportOperationStatus.Running; return true;
            case "succeeded": status = ImportOperationStatus.Succeeded; return true;
            case "failed": status = ImportOperationStatus.Failed; return true;
            default: status = default; return false;
        }
    }
}
