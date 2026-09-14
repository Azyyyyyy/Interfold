using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class HostPathsTests
{
    [Test]
    public async Task BootstrapperFileNameIsPlatformSpecific()
    {
        var name = HostPaths.BootstrapperFileName;
        await Assert.That(name == HostPaths.UnixFileName || name == HostPaths.WindowsFileName).IsTrue();
        await Assert.That(HostPaths.UnixFileName).IsEqualTo("interfold-bootstrap");
        await Assert.That(HostPaths.WindowsFileName).IsEqualTo("interfold-bootstrap.exe");

        if (OperatingSystem.IsWindows())
        {
            await Assert.That(name).IsEqualTo(HostPaths.WindowsFileName);
        }
        else
        {
            await Assert.That(name).IsEqualTo(HostPaths.UnixFileName);
        }
    }

    [Test]
    public async Task OutputDirsEqualUsesHostPathComparison()
    {
        using var scratch = TestSupport.NewScratchDir("host-paths");
        var a = scratch.Path;
        var b = Path.GetFullPath(scratch.Path);
        await Assert.That(HostPaths.OutputDirsEqual(a, b)).IsTrue();

        if (OperatingSystem.IsWindows())
        {
            var mixed = a.ToUpperInvariant();
            await Assert.That(HostPaths.OutputDirsEqual(a, mixed)).IsTrue();
        }
    }

    [Test]
    public async Task ResolveBootstrapperBinaryHonoursOverride()
    {
        using var scratch = TestSupport.NewScratchDir("host-paths-bin");
        var overridePath = Path.Combine(scratch.Path, "custom-bootstrap");
        await File.WriteAllTextAsync(overridePath, "");

        var resolved = HostPaths.ResolveBootstrapperBinary(overridePath);
        await Assert.That(resolved).IsEqualTo(Path.GetFullPath(overridePath));

        var defaulted = HostPaths.ResolveBootstrapperBinary(null);
        await Assert.That(Path.GetFileName(defaulted)).IsEqualTo(HostPaths.BootstrapperFileName);
    }

    [Test]
    public async Task DockerDesktopCandidatesCoverMachineAndUserInstalls()
    {
        var candidates = HostPaths.DockerDesktopExecutableCandidates();
        await Assert.That(candidates.Count).IsEqualTo(2);
        await Assert.That(candidates[0]).Contains("Docker Desktop.exe");
        await Assert.That(candidates[1]).Contains("Docker Desktop.exe");
        await Assert.That(candidates[0]).Contains(Path.Combine("Docker", "Docker"));
        await Assert.That(candidates[1]).Contains(Path.Combine("Programs", "DockerDesktop"));
    }

    [Test]
    public async Task FindDockerDesktopExecutableMatchesCandidatesWhenPresent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var found = HostPaths.FindDockerDesktopExecutable();
        if (found is null)
        {
            return;
        }

        await Assert.That(File.Exists(found)).IsTrue();
        await Assert.That(Path.GetFileName(found)).IsEqualTo("Docker Desktop.exe");
        await Assert.That(HostPaths.DockerDesktopExecutableCandidates().Contains(found)).IsTrue();
    }
}
