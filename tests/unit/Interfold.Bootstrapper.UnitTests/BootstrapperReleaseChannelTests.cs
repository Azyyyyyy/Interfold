using Interfold.Bootstrapper.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class BootstrapperReleaseChannelTests
{
    [Test]
    public async Task ParseWireAcceptsRollingChannels()
    {
        await Assert.That(BootstrapperReleaseChannel.ParseWire("stable"))
            .IsEqualTo(BootstrapperReleaseChannel.Stable);
        await Assert.That(BootstrapperReleaseChannel.ParseWire("bleeding-edge"))
            .IsEqualTo(BootstrapperReleaseChannel.BleedingEdge);
        await Assert.That(BootstrapperReleaseChannel.ParseWire("bleedingedge"))
            .IsEqualTo(BootstrapperReleaseChannel.BleedingEdge);
        await Assert.That(BootstrapperReleaseChannel.ParseWire(null))
            .IsEqualTo(BootstrapperReleaseChannel.Stable);
    }

    [Test]
    public async Task ParseWireAcceptsPinTags()
    {
        var pin = BootstrapperReleaseChannel.ParseWire("bootstrap-v0.0.1");
        await Assert.That(pin.IsPinned).IsTrue();
        await Assert.That(pin.ToWireValue()).IsEqualTo("bootstrap-v0.0.1");
        await Assert.That(pin.ToGitHubReleaseTag()).IsEqualTo("bootstrap-v0.0.1");
    }

    [Test]
    public async Task ParseWireNormalizesPinCase()
    {
        var pin = BootstrapperReleaseChannel.ParseWire("Bootstrap-V1.2.3-rc.1");
        await Assert.That(pin.ToWireValue()).IsEqualTo("bootstrap-v1.2.3-rc.1");
    }

    [Test]
    public async Task RollingChannelsMapToGitHubTags()
    {
        await Assert.That(BootstrapperReleaseChannel.Stable.ToGitHubReleaseTag()).IsEqualTo("latest");
        await Assert.That(BootstrapperReleaseChannel.BleedingEdge.ToGitHubReleaseTag())
            .IsEqualTo("bleeding-edge");
        await Assert.That(BootstrapperReleaseChannel.Stable.IsPinned).IsFalse();
    }

    [Test]
    public async Task ParseWireRejectsUnknownValues()
    {
        await Assert.That(() => BootstrapperReleaseChannel.ParseWire("nightly"))
            .Throws<InvalidOperationException>();
        await Assert.That(() => BootstrapperReleaseChannel.ParseWire("0.0.1"))
            .Throws<InvalidOperationException>();
        await Assert.That(() => BootstrapperReleaseChannel.ParseWire("v0.0.1"))
            .Throws<InvalidOperationException>();
        await Assert.That(() => BootstrapperReleaseChannel.ParseWire("bootstrap-v"))
            .Throws<InvalidOperationException>();
        await Assert.That(() => BootstrapperReleaseChannel.ParseWire("v"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task JsonRoundTripsPinAndRolling()
    {
        foreach (var wire in new[] { "stable", "bleeding-edge", "bootstrap-v0.0.1" })
        {
            var config = new BootstrapConfig
            {
                Deployment =
                {
                    Update =
                    {
                        Bootstrapper =
                        {
                            Channel = BootstrapperReleaseChannel.ParseWire(wire),
                        },
                    },
                },
            };
            var json = System.Text.Json.JsonSerializer.Serialize(
                config, BootstrapJsonContext.Default.BootstrapConfig);
            var loaded = System.Text.Json.JsonSerializer.Deserialize(
                json, BootstrapJsonContext.Default.BootstrapConfig);
            await Assert.That(loaded!.Deployment.Update.Bootstrapper.Channel.ToWireValue())
                .IsEqualTo(wire);
        }
    }
}
