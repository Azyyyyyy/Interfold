namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>Copies a fixture Python mock into DinD and waits until it answers /client/v4/zones.</summary>
internal static class CloudflareApiMock
{
    internal static string TunnelScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "cf_api_mock.py");

    internal static string AccessScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "cf_access_api_mock.py");

    internal static async Task<ExecResult> StartAsync(
        DinDFixtureBase dinD,
        string hostScriptPath,
        string containerDir,
        int port,
        string logPath,
        string pidPath)
    {
        await dinD.ExecAsync(["mkdir", "-p", containerDir]).ConfigureAwait(false);
        await dinD.CopyInAsync(hostScriptPath, $"{containerDir}/server.py").ConfigureAwait(false);
        return await dinD.ExecAsync(["sh", "-c", $"""
            set -e
            python3 {containerDir}/server.py {port} >{logPath} 2>&1 &
            echo $! > {pidPath}
            for i in $(seq 1 30); do
              curl -sf "http://127.0.0.1:{port}/client/v4/zones" >/dev/null && exit 0
              sleep 0.2
            done
            echo "mock CF API failed to start" >&2
            cat {logPath} >&2 || true
            exit 1
            """]).ConfigureAwait(false);
    }

    internal static Task<ExecResult> StopAsync(DinDFixtureBase dinD, string pidPath)
        => dinD.ExecAsync(["sh", "-c", $"kill $(cat {pidPath}) 2>/dev/null || true"]);
}
