namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>Copies <c>setup-fake-release-mirror.sh</c> into DinD and starts a local release HTTP mirror.</summary>
internal static class FakeReleaseMirror
{
    internal static string ScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "setup-fake-release-mirror.sh");

    internal static async Task<ExecResult> StartAsync(
        DinDFixtureBase dinD,
        string sharedBootstrapper,
        string privateBootstrapper,
        string releaseRoot,
        int port,
        string pidPath,
        string logPath)
    {
        const string containerScript = "/tmp/setup-fake-release-mirror.sh";
        await dinD.CopyInAsync(ScriptPath, containerScript).ConfigureAwait(false);
        await dinD.ExecAsync(["sed", "-i", "s/\\r$//", containerScript]).ConfigureAwait(false);
        await dinD.ExecAsync(["chmod", "+x", containerScript]).ConfigureAwait(false);
        return await dinD.ExecAsync([
            "sh", containerScript,
            sharedBootstrapper,
            privateBootstrapper,
            releaseRoot,
            port.ToString(),
            pidPath,
            logPath,
        ]).ConfigureAwait(false);
    }

    internal static Task<ExecResult> StopAsync(DinDFixtureBase dinD, string pidPath)
        => dinD.ExecAsync(["sh", "-c", $"kill $(cat {pidPath}) 2>/dev/null || true"]);
}
