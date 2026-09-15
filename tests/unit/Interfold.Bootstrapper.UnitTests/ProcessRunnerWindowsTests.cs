using Interfold.Bootstrapper.UnitTests.Attributes;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

[RequiresWindows]
public sealed class ProcessRunnerWindowsTests
{
    [Test]
    public async Task ExistsOnPathFindsCmdOnWindows()
    {
        await Assert.That(await ProcessRunner.ExistsOnPathAsync("cmd")).IsTrue();
        await Assert.That(await ProcessRunner.ExistsOnPathAsync($"missing-{Guid.NewGuid():N}")).IsFalse();
    }
}
