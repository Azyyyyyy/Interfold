namespace Interfold.Contracts.Models.ImportOperations;

/// <summary>
/// Stable machine codes for terminal import failures, persisted to the
/// <c>import_operations.error_code</c> column (operators grep/alert on the exact strings —
/// the wire spellings from <c>ToWireValue()</c> are frozen). Both paths are typed: writes
/// emit <c>ToWireValue()</c>, reads rehydrate via the tolerant <c>TryParse</c> (unknown
/// legacy spellings resolve to null rather than throwing).
/// </summary>
public enum ImportErrorCode
{
    /// <summary>The Simply Plural importer returned a graceful failure.</summary>
    SpImportFailed,

    /// <summary>Simply Plural rejected the supplied token.</summary>
    SpAuthFailed,

    /// <summary>Generic fallback when a runner failed without a specific code.</summary>
    ImportFailed,

    /// <summary>No <c>IImportJobRunner</c> is registered for the operation kind (DI misregistration).</summary>
    NoRunner,

    /// <summary>The row was stuck in Running when a fresh host booted; failed by the startup sweep.</summary>
    HostRestart,

    /// <summary>The worker was cancelled mid-job by host shutdown.</summary>
    HostShutdown,

    /// <summary>The runner threw an unhandled exception.</summary>
    Exception,
}

public static class ImportErrorCodeExtensions
{
    public static string ToWireValue(this ImportErrorCode code) => code switch
    {
        ImportErrorCode.SpImportFailed => "sp_import_failed",
        ImportErrorCode.SpAuthFailed => "sp_auth_failed",
        ImportErrorCode.ImportFailed => "import_failed",
        ImportErrorCode.NoRunner => "no_runner",
        ImportErrorCode.HostRestart => "host_restart",
        ImportErrorCode.HostShutdown => "host_shutdown",
        ImportErrorCode.Exception => "exception",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unhandled ImportErrorCode."),
    };

    /// <summary>Tolerant reverse mapping for DB reads: unknown/legacy spellings return null.</summary>
    public static ImportErrorCode? TryParse(string? raw) => raw switch
    {
        "sp_import_failed" => ImportErrorCode.SpImportFailed,
        "sp_auth_failed" => ImportErrorCode.SpAuthFailed,
        "import_failed" => ImportErrorCode.ImportFailed,
        "no_runner" => ImportErrorCode.NoRunner,
        "host_restart" => ImportErrorCode.HostRestart,
        "host_shutdown" => ImportErrorCode.HostShutdown,
        "exception" => ImportErrorCode.Exception,
        _ => null,
    };
}
