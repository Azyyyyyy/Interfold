using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class ProcessRunnerWindowsTests
{
    [Test]
    public async Task ExistsOnPathFindsCmdOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await Assert.That(await ProcessRunner.ExistsOnPathAsync("cmd")).IsTrue();
        await Assert.That(await ProcessRunner.ExistsOnPathAsync($"missing-{Guid.NewGuid():N}")).IsFalse();
    }
}
