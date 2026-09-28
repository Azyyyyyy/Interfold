using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class BootstrapperVersionTests
{
    [Test]
    public async Task HigherSemVerRemoteIsAvailableWithOrWithoutVPrefix()
    {
        await Assert.That(BootstrapperVersion.IsUpdateAvailable("999.0.0", force: false)).IsTrue();
        await Assert.That(BootstrapperVersion.IsUpdateAvailable("v999.0.0", force: false)).IsTrue();
    }

    [Test]
    public async Task ForceAlwaysReportsUpdateAvailable()
    {
        await Assert.That(BootstrapperVersion.IsUpdateAvailable("0.0.0", force: true)).IsTrue();
        await Assert.That(BootstrapperVersion.IsUpdateAvailable("v0.0.0", force: true)).IsTrue();
    }

    [Test]
    public async Task IdenticalCoreAfterNormalizeIsNotAnUpdate()
    {
        var local = BootstrapperVersion.InformationalVersion;
        var plus = local.IndexOf('+');
        var core = (plus >= 0 ? local[..plus] : local).Trim();
        if (core.Length >= 2 && (core[0] is 'v' or 'V') && char.IsAsciiDigit(core[1]))
        {
            core = core[1..];
        }

        await Assert.That(BootstrapperVersion.IsUpdateAvailable(core, force: false)).IsFalse();
        if (core.Length > 0 && char.IsAsciiDigit(core[0]))
        {
            await Assert.That(BootstrapperVersion.IsUpdateAvailable("v" + core, force: false)).IsFalse();
        }
    }

    [Test]
    public async Task SameSemVerCoreDifferentBuildMetadataIsAnUpdate()
    {
        var local = BootstrapperVersion.InformationalVersion;
        var plus = local.IndexOf('+');
        var core = (plus >= 0 ? local[..plus] : local).Trim();
        if (core.Length >= 2 && (core[0] is 'v' or 'V') && char.IsAsciiDigit(core[1]))
        {
            core = core[1..];
        }

        // Rolling stamps share a SemVer core; differing +commit metadata must still update.
        await Assert.That(BootstrapperVersion.IsUpdateAvailable($"{core}+deadbeef", force: false)).IsTrue();
    }

    [Test]
    public async Task UnitTestAssemblyHasNoReleaseChannelStamp()
    {
        // CI publish stamps AssemblyMetadata; local/unit builds omit it.
        await Assert.That(BootstrapperVersion.ReleaseChannelWire).IsNull();
        await Assert.That(BootstrapperVersion.BuiltReleaseChannel).IsNull();
    }

    [Test]
    public async Task UserAgentOmitsChannelWhenUnstamped()
    {
        await Assert.That(BootstrapperVersion.UserAgent)
            .IsEqualTo($"interfold-bootstrap/{BootstrapperVersion.InformationalVersion}");
    }
}
