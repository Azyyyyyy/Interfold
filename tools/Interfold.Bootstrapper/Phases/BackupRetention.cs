namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Pure retention helper for backup archives. Returns the oldest-by-mtime files that exceed
/// the keep count so callers can delete them. Ordering is oldest-first.
/// </summary>
internal static class BackupRetention
{
    /// <summary>
    /// Returns the files that should be deleted to leave at most <paramref name="keep"/>
    /// newest archives. Empty when <paramref name="files"/> has ≤ keep entries.
    /// </summary>
    public static IEnumerable<FileInfo> Prune(IEnumerable<FileInfo> files, int keep)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (keep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keep), keep, "keep must be >= 0.");
        }

        var ordered = files
            .OrderBy(f => f.LastWriteTimeUtc)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        if (ordered.Count <= keep)
        {
            return [];
        }

        var drop = ordered.Count - keep;
        return ordered.Take(drop);
    }
}
