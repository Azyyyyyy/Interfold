using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class LocalNameHostsTests
{
    [Test]
    public async Task SubdomainLocalNamesMapToThisComputer()
    {
        var config = new BootstrapConfig
        {
            Edge = new EdgeSection
            {
                Routing = new EdgeRoutingSection
                {
                    Mode = EdgeRoutingMode.Subdomain,
                    ApiHost = "api.AP-MAIN.local",
                    WebHost = "web.AP-MAIN.local",
                },
            },
        };

        var names = LocalNameHosts.Aliases(config);
        var merged = LocalNameHosts.Merge("127.0.0.1 localhost\n", names);

        await Assert.That(names).IsEquivalentTo(["api.AP-MAIN.local", "web.AP-MAIN.local"]);
        await Assert.That(merged).Contains("127.0.0.1 api.AP-MAIN.local web.AP-MAIN.local");
        await Assert.That(merged).Contains("::1 api.AP-MAIN.local web.AP-MAIN.local");
        await Assert.That(LocalNameHosts.Merge(merged, names)).IsEqualTo(merged);
    }

    [Test]
    public async Task PathModeAndPublicNamesLeaveTheHostsFileAlone()
    {
        var subdomain = new BootstrapConfig
        {
            Edge = new EdgeSection
            {
                Routing = new EdgeRoutingSection
                {
                    Mode = EdgeRoutingMode.Subdomain,
                    ApiHost = "api.example.com",
                    WebHost = "web.example.com",
                },
            },
        };
        var path = new BootstrapConfig();
        var withBlock = LocalNameHosts.Merge(
            "127.0.0.1 localhost\n",
            ["api.box.local"]);

        await Assert.That(LocalNameHosts.Aliases(subdomain)).IsEmpty();
        await Assert.That(LocalNameHosts.Aliases(path)).IsEmpty();
        await Assert.That(LocalNameHosts.Merge(withBlock, [])).IsEqualTo("127.0.0.1 localhost\n");
    }

    [Test]
    public async Task WritableHostsFileDoesNotAskForAdministrator()
    {
        var path = Path.Combine(Path.GetTempPath(), "interfold-hosts-" + Guid.NewGuid().ToString("n"));
        var asked = false;
        try
        {
            LocalNameHosts.Apply(
                LocalConfig(),
                Logger(),
                path,
                canPrompt: true,
                confirmElevation: () => { asked = true; return false; });

            await Assert.That(File.ReadAllText(path)).Contains("api.box.local");
            await Assert.That(asked).IsFalse();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task AccessDeniedAsksBeforeAnAdministratorWrite()
    {
        string? written = null;
        var asked = false;
        LocalNameHosts.Apply(
            LocalConfig(),
            Logger(),
            Path.Combine(Path.GetTempPath(), "interfold-hosts-missing"),
            canPrompt: true,
            confirmElevation: () => { asked = true; return true; },
            write: (_, _) => throw new UnauthorizedAccessException("denied"),
            writeElevated: (_, content) => { written = content; return true; });

        await Assert.That(asked).IsTrue();
        await Assert.That(written).IsNotNull();
        await Assert.That(written!).Contains("127.0.0.1 api.box.local web.box.local");
    }

    [Test]
    public async Task DeclinedAdministratorPromptSkipsTheElevatedWrite()
    {
        var elevated = false;
        LocalNameHosts.Apply(
            LocalConfig(),
            Logger(),
            Path.Combine(Path.GetTempPath(), "interfold-hosts-missing"),
            canPrompt: true,
            confirmElevation: () => false,
            write: (_, _) => throw new UnauthorizedAccessException("denied"),
            writeElevated: (_, _) => { elevated = true; return true; });

        await Assert.That(elevated).IsFalse();
    }

    [Test]
    public async Task NonInteractiveAccessDeniedDoesNotAsk()
    {
        var asked = false;
        var elevated = false;
        LocalNameHosts.Apply(
            LocalConfig(),
            Logger(),
            Path.Combine(Path.GetTempPath(), "interfold-hosts-missing"),
            canPrompt: false,
            confirmElevation: () => { asked = true; return true; },
            write: (_, _) => throw new UnauthorizedAccessException("denied"),
            writeElevated: (_, _) => { elevated = true; return true; });

        await Assert.That(asked).IsFalse();
        await Assert.That(elevated).IsFalse();
    }

    private static BootstrapConfig LocalConfig() => new()
    {
        Edge = new EdgeSection
        {
            Routing = new EdgeRoutingSection
            {
                Mode = EdgeRoutingMode.Subdomain,
                ApiHost = "api.box.local",
                WebHost = "web.box.local",
            },
        },
    };

    private static PhaseLogger Logger() =>
        new(TestSupport.MakeOptions(outputDir: Path.GetTempPath()));
}
