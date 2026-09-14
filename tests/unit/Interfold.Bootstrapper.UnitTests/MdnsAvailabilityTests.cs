using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class MdnsAvailabilityTests
{
    [Test]
    public async Task SupportsAutoInstallIsLinuxPackageFamiliesOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(MdnsAvailability.SupportsAutoInstall(DistroFamily.Debian)).IsFalse();
            await Assert.That(MdnsAvailability.SupportsAutoInstall(DistroFamily.RedHat)).IsFalse();
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            await Assert.That(MdnsAvailability.SupportsAutoInstall(DistroFamily.Debian)).IsFalse();
            return;
        }

        await Assert.That(MdnsAvailability.SupportsAutoInstall(DistroFamily.Debian)).IsTrue();
        await Assert.That(MdnsAvailability.SupportsAutoInstall(DistroFamily.RedHat)).IsTrue();
        await Assert.That(MdnsAvailability.SupportsAutoInstall(DistroFamily.Unknown)).IsFalse();
    }

    [Test]
    public async Task ManualInstallHintIsPlatformSpecific()
    {
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(MdnsAvailability.ManualInstallHint(DistroFamily.Unknown))
                .Contains("Bonjour");
            return;
        }

        await Assert.That(MdnsAvailability.ManualInstallHint(DistroFamily.Debian)).Contains("avahi-daemon");
        await Assert.That(MdnsAvailability.ManualInstallHint(DistroFamily.RedHat)).Contains("nss-mdns");
    }

    [Test]
    public async Task WindowsProbeResolvesLocalhost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await MdnsAvailability.IsHostnameResolvableAsync("localhost", CancellationToken.None);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task WindowsProbeRejectsNonsenseHostname()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await MdnsAvailability.IsHostnameResolvableAsync(
            $"no-such-host-{Guid.NewGuid():N}.invalid",
            CancellationToken.None);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task TryInstallAvahiReturnsFalseOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new PhaseLogger(TestSupport.MakeOptions(outputDir: Path.GetTempPath()));
        var distro = new DistroInfo(
            Id: "windows",
            IdLike: null,
            PrettyName: "Windows",
            VersionId: null,
            VersionCodename: null,
            Family: DistroFamily.Unknown);

        var installed = await MdnsAvailability.TryInstallAvahiAsync(distro, logger, CancellationToken.None);
        await Assert.That(installed).IsFalse();
    }
}
