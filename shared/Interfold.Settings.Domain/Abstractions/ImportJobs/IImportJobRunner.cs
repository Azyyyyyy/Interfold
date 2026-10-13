using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Settings.Domain.Abstractions.ImportJobs;

/// <summary>Per-format importer used by the background worker. Worker matches
/// <see cref="Format"/> to the dequeued <see cref="ImportJobItem.Format"/>. Implementations
/// live wherever the downstream client lives (SP runner in Interfold.Api; PK is a stub).</summary>
public interface IImportJobRunner
{
    ImportFormat Format { get; }

    /// <summary>Runs the import. MUST NOT throw on graceful failure — return
    /// <see cref="ImportJobOutcome.Success"/> = false with a populated ErrorCode. Any
    /// escaped exception is caught by the worker and treated as <c>error_code = "exception"</c>.</summary>
    Task<ImportJobOutcome> RunAsync(ImportJobItem item, CancellationToken cancellationToken = default);
}

/// <summary>Import result mirroring the worker's terminal-state vocabulary.</summary>
public sealed record ImportJobOutcome(
    bool Success,
    int AlterCount,
    ImportErrorCode? ErrorCode = null,
    string? ErrorMessage = null);
