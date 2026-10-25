namespace Interfold.AppHost;

/// <summary>Repo-root-relative paths the AppHost graph bind-mounts.</summary>
public static class AppHostRepoPaths
{
    /// <summary>Walks up from <see cref="AppContext.BaseDirectory"/> until it finds
    /// <c>Interfold.slnx</c>.</summary>
    public static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Interfold.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate 'Interfold.slnx' walking up from '{AppContext.BaseDirectory}'.");
    }
}
