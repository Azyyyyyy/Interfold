using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class PathLookupTests
{
    [Test]
    public async Task WindowsResolvesBareCommandViaPathExt()
    {
        using var scratch = TestSupport.NewScratchDir("path-lookup");
        var exe = Path.Combine(scratch.Path, "docker.exe");
        await File.WriteAllTextAsync(exe, "");

        var found = PathLookup.TryFind("docker", scratch.Path, ".COM;.EXE;.BAT", windows: true);
        await Assert.That(found).IsEqualTo(exe);
        await Assert.That(PathLookup.Exists("docker", scratch.Path, ".COM;.EXE;.BAT", windows: true)).IsTrue();
    }

    [Test]
    public async Task WindowsHonoursExplicitExtension()
    {
        using var scratch = TestSupport.NewScratchDir("path-lookup-ext");
        var exe = Path.Combine(scratch.Path, "docker.exe");
        await File.WriteAllTextAsync(exe, "");

        await Assert.That(PathLookup.Exists("docker.exe", scratch.Path, ".BAT", windows: true)).IsTrue();
        await Assert.That(PathLookup.Exists("docker", scratch.Path, ".BAT", windows: true)).IsFalse();
    }

    [Test]
    public async Task UnixUsesColonSeparatedPath()
    {
        using var scratch = TestSupport.NewScratchDir("path-lookup-unix");
        var binary = Path.Combine(scratch.Path, "docker");
        await File.WriteAllTextAsync(binary, "");

        await Assert.That(PathLookup.Exists("docker", scratch.Path, pathExt: null, windows: false)).IsTrue();
        await Assert.That(PathLookup.Exists("missing", scratch.Path, pathExt: null, windows: false)).IsFalse();
    }

    [Test]
    public async Task EmptyPathReturnsFalse()
    {
        await Assert.That(PathLookup.Exists("docker", pathEnv: null, pathExt: ".EXE", windows: true)).IsFalse();
        await Assert.That(PathLookup.Exists("docker", pathEnv: "", pathExt: ".EXE", windows: true)).IsFalse();
        await Assert.That(PathLookup.Exists("", pathEnv: "/usr/bin", pathExt: null, windows: false)).IsFalse();
    }
}
