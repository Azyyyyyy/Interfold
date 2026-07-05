namespace Interfold.Contracts.Models.ImportOperations;

/// <summary>
/// Stable machine codes for terminal import failures, persisted to the
/// <c>import_operations.error_code</c> column (operators grep/alert on the exact strings —
/// the wire spellings from <c>ToWireValue()</c> are frozen). The write path is typed with
/// this enum; <see cref="ImportOperationSnapshot.ErrorCode"/> stays a raw string on the
/// read side so unknown legacy row values surface verbatim instead of failing to parse.
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
}
