using Interfold.Bootstrapper.UnitTests.Attributes;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

[RequiresWindows]
[NotInParallel("process-runner-path")]
public sealed class ProcessRunnerWindowsTests
{
    [Test]
    public async Task ExistsOnPathFindsCmdOnWindows()
    {
        await Assert.That(await ProcessRunner.ExistsOnPathAsync("cmd")).IsTrue();
        await Assert.That(await ProcessRunner.ExistsOnPathAsync($"missing-{Guid.NewGuid():N}")).IsFalse();
    }

    [Test]
    public async Task RunAsyncResolvesCmdShimFromPath()
    {
        using var scratch = TestSupport.NewScratchDir("npm-shim");
        await File.WriteAllTextAsync(Path.Combine(scratch.Path, "hello.cmd"), "@echo hello-ok");
        var prior = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", scratch.Path + ";" + prior);
        try
        {
            var result = await ProcessRunner.RunAsync("hello", []);
            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.StdOut).Contains("hello-ok");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", prior);
        }
    }
}
