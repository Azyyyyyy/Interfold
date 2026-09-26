using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

[NotInParallel("discord-oidc-source-env")]
public sealed class DiscordOidcWorkerSourceTests
{
    [Test]
    public async Task WriteConfigMatchesErisaShape()
    {
        using var scratch = TestSupport.NewScratchDir("discord-oidc-config");
        DiscordOidcWorkerSource.WriteConfig(
            scratch.Path,
            "client-1",
            "secret-1",
            "https://team.cloudflareaccess.com/cdn-cgi/access/callback");

        var json = await File.ReadAllTextAsync(Path.Combine(scratch.Path, "config.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        await Assert.That(doc.RootElement.GetProperty("clientId").GetString()).IsEqualTo("client-1");
        await Assert.That(doc.RootElement.GetProperty("clientSecret").GetString()).IsEqualTo("secret-1");
        await Assert.That(doc.RootElement.GetProperty("redirectURL").GetString())
            .IsEqualTo("https://team.cloudflareaccess.com/cdn-cgi/access/callback");
        await Assert.That(doc.RootElement.GetProperty("serversToCheckRolesFor").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task ToConfigEsModuleExportsJsonModuleNamespace()
    {
        var module = DiscordOidcWorkerSource.ToConfigEsModule(
            """{"clientId":"id-1","clientSecret":"sec","redirectURL":"https://cb","serversToCheckRolesFor":[]}""");
        await Assert.That(module).Contains("export default json");
        await Assert.That(module).Contains(
            "export const { clientId, clientSecret, redirectURL, serversToCheckRolesFor } = json");
        await Assert.That(module).Contains("\"clientId\":\"id-1\"");
    }

    [Test]
    public async Task ToConfigEsModuleRejectsNonObject()
    {
        await Assert.That(() => DiscordOidcWorkerSource.ToConfigEsModule("[1]"))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task EnsureWorkDirectoryCopiesSourceOverride()
    {
        using var source = TestSupport.NewScratchDir("discord-oidc-src");
        using var output = TestSupport.NewScratchDir("discord-oidc-out");
        await File.WriteAllTextAsync(Path.Combine(source.Path, "worker.js"), "export default {}");
        await File.WriteAllTextAsync(
            Path.Combine(source.Path, "package.json"),
            """{"dependencies":{"hono":"^4.6.20","jose":"^5.9.6"}}""");

        Environment.SetEnvironmentVariable(DiscordOidcWorkerSource.SourceDirEnv, source.Path);
        try
        {
            using var http = new HttpClient();
            var work = await DiscordOidcWorkerSource.EnsureWorkDirectoryAsync(
                output.Path,
                http,
                new PhaseLogger(TestSupport.MakeOptions(outputDir: output.Path)),
                CancellationToken.None);
            await Assert.That(File.Exists(Path.Combine(work, "worker.js"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(work, "package.json"))).IsTrue();
            await Assert.That(work).IsEqualTo(DiscordOidcWorkerSource.CacheDirectory(output.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DiscordOidcWorkerSource.SourceDirEnv, null);
        }
    }

    [Test]
    public async Task ReadRuntimeDependencyVersionsFromPackageJson()
    {
        using var scratch = TestSupport.NewScratchDir("discord-oidc-pkg");
        await File.WriteAllTextAsync(
            Path.Combine(scratch.Path, "package.json"),
            """{"dependencies":{"hono":"^4.6.20","jose":"^5.9.6","wrangler":"3.107.2"}}""");

        var versions = DiscordOidcWorkerBundler.ReadRuntimeDependencyVersions(scratch.Path);
        await Assert.That(versions.Hono).IsEqualTo("^4.6.20");
        await Assert.That(versions.Jose).IsEqualTo("^5.9.6");
    }

    [Test]
    public async Task HasDiscordOAuthRequiresBothIdAndSecret()
    {
        await Assert.That(CloudflareAccessPhase.HasDiscordOAuth(new ApiOAuthSection
        {
            DiscordClientId = "id",
            DiscordClientSecret = "secret",
        })).IsTrue();
        await Assert.That(CloudflareAccessPhase.HasDiscordOAuth(new ApiOAuthSection
        {
            DiscordClientId = "id",
        })).IsFalse();
        await Assert.That(CloudflareAccessPhase.HasDiscordOAuth(new ApiOAuthSection())).IsFalse();
    }

    [Test]
    public async Task FileUrlPinsCommitAndFile()
    {
        await Assert.That(DiscordOidcWorkerSource.FileUrl(
                DiscordOidcWorkerSource.DefaultFetchBaseUrl,
                "worker.js"))
            .IsEqualTo(
                $"https://raw.githubusercontent.com/Erisa/discord-oidc-worker/{DiscordOidcWorkerSource.PinnedSha}/worker.js");
    }
}
