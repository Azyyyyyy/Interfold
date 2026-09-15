namespace Interfold.Bootstrapper.Util;

/// <summary>
/// PATH / PATHEXT scan used by <see cref="ProcessRunner.ExistsOnPathAsync"/> on Windows
/// (where <c>/bin/sh -c command -v</c> does not exist). Pure over injected env so unit
/// tests can drive it without mutating the process PATH.
/// </summary>
internal static class PathLookup
{
    private const string DefaultWindowsPathExt = ".COM;.EXE;.BAT;.CMD";

    public static bool ExistsOnWindowsPath(string command) =>
        TryFind(command,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT") ?? DefaultWindowsPathExt,
            windows: true) is not null;

    /// <summary>
    /// Returns true when <paramref name="command"/> resolves to a file under
    /// <paramref name="pathEnv"/>. On Windows, <paramref name="pathExt"/> supplies the
    /// PATHEXT suffixes applied when the command has no extension.
    /// </summary>
    internal static bool Exists(string command, string? pathEnv, string? pathExt, bool windows)
        => TryFind(command, pathEnv, pathExt, windows) is not null;

    internal static string? TryFind(string command, string? pathEnv, string? pathExt, bool windows)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        var separator = windows ? ';' : ':';
        var names = CandidateNames(command, pathExt, windows);
        foreach (var dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                if (windows)
                {
                    // Real Windows FS is case-insensitive; PATHEXT is conventionally
                    // uppercase (.EXE). Unit tests (and DinD) often run this path on Linux,
                    // so match candidate names with OrdinalIgnoreCase against the directory.
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        var fileName = Path.GetFileName(file);
                        foreach (var name in names)
                        {
                            if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase))
                            {
                                return file;
                            }
                        }
                    }

                    continue;
                }

                foreach (var name in names)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // PATH entries can be malformed or unreadable; skip and keep scanning.
            }
        }

        return null;
    }

    private static string[] CandidateNames(string command, string? pathExt, bool windows)
    {
        if (!windows || Path.GetExtension(command).Length > 0)
        {
            return [command];
        }

        var exts = (pathExt ?? DefaultWindowsPathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = new string[exts.Length];
        for (var i = 0; i < exts.Length; i++)
        {
            var ext = exts[i];
            names[i] = ext.StartsWith('.') ? command + ext : command + "." + ext;
        }
        return names;
    }
}
