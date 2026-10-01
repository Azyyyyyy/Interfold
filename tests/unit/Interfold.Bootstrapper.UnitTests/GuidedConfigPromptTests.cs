using System.Net;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Shared.Contracts;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>Drives <see cref="GuidedConfigPrompt"/> through Spectre <c>TestConsole</c>.
/// Selection prompts take Enter for the highlighted (first) choice and DownArrow to move.
/// Sign-in is a multi-select: Space ticks the highlighted provider and Enter accepts.
/// Providers already in the file start ticked, so Enter keeps them.
/// Confirmation prompts require Enter after <c>y</c>/<c>n</c>; Enter alone accepts the default.
/// <para><c>[NotInParallel("bootstrapper-console")]</c> shares the Spectre TestConsole lock
/// with <see cref="ConfigInteractivePromptTests"/>.</para></summary>
[NotInParallel("bootstrapper-console")]
[Retry(2)]
public sealed class GuidedConfigPromptTests
{
    private const int AdvancedFieldCount = 61;

    private static TestConsole NewConsole()
    {
        var console = new TestConsole();
        console.Interactive();
        console.Profile.Height = 120;
        console.Profile.Width = 130;
        return console;
    }

    private static void Select(TestConsole console, int downArrows)
    {
        for (var i = 0; i < downArrows; i++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        console.Input.PushKey(ConsoleKey.Enter);
    }

    private static void Accept(TestConsole console) => console.Input.PushKey(ConsoleKey.Enter);

    private static void Tick(TestConsole console, int downArrows)
    {
        for (var i = 0; i < downArrows; i++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.Enter);
    }

    private static void DeclineOptional(TestConsole console)
    {
        Accept(console);
        Accept(console);
        Accept(console);
    }

    private static void Answer(TestConsole console, string text) => console.Input.PushTextWithEnter(text);

    private static void ConfirmAdvanced(TestConsole console) => Select(console, AdvancedFieldCount);

    private static BootstrapConfig Run(
        TestConsole console,
        string? hostname = null,
        IPAddress? ip = null,
        BootstrapConfig? existing = null) =>
        GuidedConfigPrompt.Run(
            console,
            maskSecrets: true,
            localAddressProbe: () => ip,
            hostnameProbe: () => hostname,
            existing: existing);

    [Test]
    public async Task ChoosingAdvancedSkipsGuidedQuestions()
    {
        var console = NewConsole();
        Select(console, downArrows: 1);
        ConfirmAdvanced(console);

        var config = Run(console);

        await Assert.That(console.Output).Contains("Configure interfold.bootstrap.json");
        await Assert.That(console.Output).DoesNotContain(GuidedConfigPrompt.ReachabilityTitle);
        await Assert.That(config.Edge.Hosts).IsEmpty();
        await Assert.That(config.Datastores.Persistence).IsEqualTo(PersistenceMode.Sqlite);
        await Assert.That(config.Edge.TlsMode).IsEqualTo(EdgeTlsMode.PrivateCa);
    }

    [Test]
    public async Task ReconfigureAdvancedKeepsTheExistingFile()
    {
        var existing = new BootstrapConfig();
        existing.Edge.Hosts = ["kept.example.com"];
        existing.Edge.Certificates.RootCaName = "Kept CA";

        var console = NewConsole();
        Select(console, downArrows: 1);
        ConfirmAdvanced(console);

        var config = Run(console, existing: existing);

        await Assert.That(console.Output).Contains("How do you want to configure Interfold?");
        await Assert.That(console.Output).DoesNotContain(GuidedConfigPrompt.ReachabilityTitle);
        await Assert.That(config.Edge.Hosts).IsEquivalentTo(["kept.example.com"]);
        await Assert.That(config.Edge.Certificates.RootCaName).IsEqualTo("Kept CA");
    }

    [Test]
    public async Task ReconfigureGuidedKeepsCurrentAnswersOnEnter()
    {
        var existing = new BootstrapConfig();
        existing.Edge.Hosts = ["kept.example.com"];
        existing.Edge.TlsMode = EdgeTlsMode.PrivateCa;
        existing.Edge.Certificates.RootCaName = "Kept CA";
        existing.Edge.Ports.Http = 8080;
        existing.Edge.Ports.Https = 8443;
        existing.Deployment.IncludeWeb = true;
        existing.Api.Image = "ghcr.io/example/interfold-api:pinned";
        existing.Api.OAuth.GoogleClientId = "google-client";
        existing.Api.OAuth.GoogleClientSecret = "google-secret";
        existing.Api.OAuth.AppleClientId = "com.example.interfold";
        existing.Api.OAuth.AppleClientSecret = "apple-secret";
        existing.Api.OAuth.CallbackBaseUrl = "https://kept.example.com:8443";
        existing.Deployment.Backup.Enabled = true;
        existing.Deployment.Backup.Schedule = "weekly";
        existing.Deployment.Backup.RetainCount = 7;
        existing.Deployment.Backup.Directory = "/var/backups/interfold";
        existing.Deployment.Update.Enabled = true;
        existing.Deployment.Update.HealthCheckTimeoutSeconds = 240;
        existing.Deployment.Update.Services = ["interfold-api"];
        existing.Deployment.Update.AutoRestoreOnFailure = true;
        existing.Deployment.Update.Bootstrapper.Enabled = true;
        existing.Deployment.Update.Bootstrapper.AutoRollbackOnFailure = true;
        existing.Deployment.Update.Bootstrapper.Channel = BootstrapperReleaseChannel.BleedingEdge;
        existing.Api.Firebase.AndroidConfigPath = "/tmp/google-services.json";
        existing.Observability.OtlpEndpoint = "https://otel.example.com/v1/traces";
        existing.Datastores.Persistence = PersistenceMode.ScyllaPostgres;

        var console = NewConsole();
        for (var i = 0; i < 25; i++)
        {
            Accept(console);
        }

        var config = Run(console, existing: existing);

        await Assert.That(config.Datastores.Persistence).IsEqualTo(PersistenceMode.Sqlite);
        await Assert.That(console.Output).DoesNotContain("Where should data be stored?");
        await Assert.That(config.Edge.Hosts).IsEquivalentTo(["kept.example.com"]);
        await Assert.That(config.Edge.Certificates.RootCaName).IsEqualTo("Kept CA");
        await Assert.That(config.Edge.Ports.Http).IsEqualTo(8080);
        await Assert.That(config.Edge.Ports.Https).IsEqualTo(8443);
        await Assert.That(config.Api.Image).IsEqualTo("ghcr.io/example/interfold-api:pinned");
        await Assert.That(config.Api.OAuth.GoogleClientId).IsEqualTo("google-client");
        await Assert.That(config.Api.OAuth.GoogleClientSecret).IsEqualTo("google-secret");
        await Assert.That(config.Api.OAuth.AppleClientSecret).IsEqualTo("apple-secret");
        await Assert.That(config.Api.OAuth.DiscordClientId).IsEmpty();
        await Assert.That(config.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://kept.example.com:8443");
        await Assert.That(config.Deployment.Backup.Directory).IsEqualTo("/var/backups/interfold");
        await Assert.That(config.Deployment.Backup.Schedule).IsEqualTo("weekly");
        await Assert.That(config.Deployment.Backup.RetainCount).IsEqualTo(7);
        await Assert.That(config.Deployment.Update.HealthCheckTimeoutSeconds).IsEqualTo(240);
        await Assert.That(config.Deployment.Update.Services).IsEquivalentTo(["interfold-api"]);
        await Assert.That(config.Deployment.Update.Bootstrapper.Channel).IsEqualTo(BootstrapperReleaseChannel.BleedingEdge);
        await Assert.That(config.Api.Firebase.AndroidConfigPath).IsEqualTo("/tmp/google-services.json");
        await Assert.That(config.Observability.OtlpEndpoint).IsEqualTo("https://otel.example.com/v1/traces");
        await Assert.That(console.Output).DoesNotContain("google-secret");
        await Assert.That(console.Output).DoesNotContain("apple-secret");
    }

    [Test]
    public async Task LanConfirmUsesPrivateCaSqliteAndPath()
    {
        var console = NewConsole();
        Accept(console);
        Accept(console);
        Accept(console);
        Answer(console, "192.168.1.42");
        Accept(console);
        Accept(console);
        Accept(console);
        DeclineOptional(console);
        Accept(console);

        var config = Run(console);

        await Assert.That(config.Edge.TlsMode).IsEqualTo(EdgeTlsMode.PrivateCa);
        await Assert.That(console.Output).DoesNotContain("Scylla and Postgres");
        await Assert.That(config.Datastores.Persistence).IsEqualTo(PersistenceMode.Sqlite);
        await Assert.That(config.Edge.Routing.Mode).IsEqualTo(EdgeRoutingMode.Path);
        await Assert.That(config.Edge.Hosts).IsEquivalentTo(["192.168.1.42"]);
        await Assert.That(config.Edge.Certificates.TrustStoreInstall).IsTrue();
        await Assert.That(console.Output).Contains("Ticked options are shown when someone signs in.");
        await Assert.That(console.Output).Contains("Unticked options stay hidden.");
        await Assert.That(console.Output).Contains("Google sign-in is not available for this address.");
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.SignInDiscord);
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.SignInApple);
        await Assert.That(console.Output).DoesNotContain(GuidedConfigPrompt.GoogleClientIdLabel);
        await Assert.That(config.Edge.Ports.Http).IsEqualTo(80);
        await Assert.That(config.Edge.Ports.Https).IsEqualTo(443);
        await Assert.That(config.Api.OAuth.AppleClientId).IsEmpty();
        await Assert.That(config.Edge.Cloudflare.Enabled).IsFalse();
        await Assert.That(config.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://192.168.1.42");
        await Assert.That(console.Output).Contains("Public API URL");
        await Assert.That(console.Output).Contains("https://192.168.1.42/api/");
        await Assert.That(console.Output).DoesNotContain("Public Web URL");
        await Assert.That(config.Deployment.Backup.Enabled).IsFalse();
        await Assert.That(config.Deployment.Update.Enabled).IsFalse();
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.UpdatesUnavailableMessage);
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.FirebaseConsolePage);
        await Assert.That(console.Output).Contains("type a name for the project");
        await Assert.That(console.Output).Contains("Choose the gear next to Project Overview, then Project settings.");
        await Assert.That(console.Output).Contains("Generate new private key");
        await Assert.That(console.Output).DoesNotContain("same Google Cloud project you created for Google sign-in");
        await Assert.That(console.Output).DoesNotContain("pick the Google Cloud project you use for sign-in");
        await Assert.That(console.Output).Contains("google-services.json");
        var firebaseHint = string.Join(
            ' ',
            console.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        await Assert.That(firebaseHint).Contains("when who is fronting changes for a system");
        await Assert.That(firebaseHint).Contains("Most people should leave this off.");
        await Assert.That(firebaseHint).Contains("OpenTelemetry collector that is already running");
        await Assert.That(firebaseHint).Contains("separate program");
        await Assert.That(firebaseHint).Contains("forwards them to a dashboard you choose");
        await Assert.That(firebaseHint).Contains(GuidedConfigPrompt.OtlpCollectorDocs);
        await Assert.That(firebaseHint).Contains("full logging from the API, and from the app");
        await Assert.That(console.Output).DoesNotContain(GuidedConfigPrompt.UpdateQuestion);
        await Assert.That(console.Output).Contains("not available (scheduled backups are off)");
        ConfigPhase.Validate(config);
    }

    [Test]
    public async Task IpOnlyHostSkipsSubdomainQuestion()
    {
        var console = NewConsole();
        Accept(console);
        Accept(console);
        Answer(console, "y");
        Answer(console, "192.168.1.42");
        Accept(console);
        Accept(console);
        Accept(console);
        DeclineOptional(console);
        Accept(console);

        var config = Run(console);

        await Assert.That(console.Output).Contains(GuidedConfigPrompt.HostQuestionLan);
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.HostHintLan);
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.TrustQuestion);
        await Assert.That(console.Output).Contains("Browsers warn that a home-network site is unsafe");
        await Assert.That(console.Output).Contains("Phones and other computers still need the certificate installed on them.");
        await Assert.That(console.Output).DoesNotContain("CIDR");
        await Assert.That(console.Output).DoesNotContain(GuidedConfigPrompt.RoutingTitle);
        await Assert.That(config.Deployment.IncludeWeb).IsTrue();
        await Assert.That(config.Edge.Routing.Mode).IsEqualTo(EdgeRoutingMode.Path);
        await Assert.That(console.Output).Contains("IP hosts stay on one address");
        await Assert.That(console.Output).Contains("Public API URL");
        await Assert.That(console.Output).Contains("Public Web URL");
        await Assert.That(console.Output).Contains("https://192.168.1.42/api/");
        await Assert.That(console.Output).Contains("https://192.168.1.42/ ");
    }

    [Test]
    public async Task IpReconfigureDropsSavedSubdomainAndHidesGoogle()
    {
        var existing = new BootstrapConfig();
        existing.Edge.Hosts = ["interfold.co.uk"];
        existing.Edge.Routing.Mode = EdgeRoutingMode.Subdomain;
        existing.Edge.Routing.ApiHost = "testapi.interfold.co.uk";
        existing.Edge.Routing.WebHost = "testweb.interfold.co.uk";
        existing.Deployment.IncludeWeb = true;
        existing.Api.OAuth.GoogleClientId = "google-client";
        existing.Api.OAuth.GoogleClientSecret = "google-secret";
        existing.Api.OAuth.CallbackBaseUrl = "https://testapi.interfold.co.uk";

        var console = NewConsole();
        Accept(console);
        Accept(console);
        Accept(console);
        Answer(console, "192.168.1.1");
        Accept(console);
        Accept(console);
        Accept(console);
        DeclineOptional(console);
        Accept(console);

        var config = Run(console, existing: existing);

        await Assert.That(config.Edge.Hosts).IsEquivalentTo(["192.168.1.1"]);
        await Assert.That(config.Edge.Routing.Mode).IsEqualTo(EdgeRoutingMode.Path);
        await Assert.That(config.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://192.168.1.1");
        await Assert.That(config.Api.OAuth.GoogleClientId).IsEmpty();
        await Assert.That(config.Api.OAuth.GoogleClientSecret).IsEmpty();
        await Assert.That(console.Output).Contains("Google sign-in is not available for this address.");
        await Assert.That(console.Output).DoesNotContain("] Google");
        await Assert.That(console.Output).DoesNotContain(GuidedConfigPrompt.GoogleClientIdLabel);
    }

    [Test]
    public async Task DnsHostWithWebCanChooseSeparateHostnames()
    {
        var console = NewConsole();
        Accept(console);
        Accept(console);
        Answer(console, "y");
        Answer(console, "app.example.com");
        Accept(console);
        Answer(console, "api");
        Answer(console, "web");
        Accept(console);
        Accept(console);
        Accept(console);
        DeclineOptional(console);
        Accept(console);

        var config = Run(console);

        await Assert.That(config.Edge.Routing.Mode).IsEqualTo(EdgeRoutingMode.Subdomain);
        await Assert.That(config.Edge.Hosts).IsEquivalentTo(["app.example.com"]);
        await Assert.That(config.Edge.Routing.ApiHost).IsEqualTo("api.app.example.com");
        await Assert.That(config.Edge.Routing.WebHost).IsEqualTo("web.app.example.com");
        await Assert.That(config.Edge.TlsMode).IsEqualTo(EdgeTlsMode.PrivateCa);
        await Assert.That(config.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://api.app.example.com");
        await Assert.That(console.Output).Contains("https://api.app.example.com");
        await Assert.That(console.Output).DoesNotContain("https://api.app.example.com/api/");
        await Assert.That(console.Output).Contains("https://web.app.example.com");
        ConfigPhase.Validate(config);
    }

    [Test]
    public async Task SeparateHostnamesAcceptFullNameUnderTheSameHost()
    {
        var console = NewConsole();
        Accept(console);
        Accept(console);
        Answer(console, "y");
        Answer(console, "app.example.com");
        Accept(console);
        Answer(console, "api.app.example.com");
        Answer(console, "web.app.example.com");
        Accept(console);
        Accept(console);
        Accept(console);
        DeclineOptional(console);
        Accept(console);

        var config = Run(console);

        await Assert.That(config.Edge.Routing.ApiHost).IsEqualTo("api.app.example.com");
        await Assert.That(config.Edge.Routing.WebHost).IsEqualTo("web.app.example.com");
        await Assert.That(config.Edge.Hosts).IsEquivalentTo(["app.example.com"]);
        ConfigPhase.Validate(config);
    }

    [Test]
    public async Task CloudflareConfirmLeavesTlsForValidateAndSkipsGoogleWhenAccessIsOff()
    {
        const string token = "cfat-test-token";
        var console = NewConsole();
        Accept(console);
        Select(console, downArrows: 1);
        Answer(console, "y");
        Answer(console, "app.example.com");
        Select(console, downArrows: 1);
        Answer(console, token);
        Accept(console);
        Accept(console);
        DeclineOptional(console);
        Accept(console);

        var config = Run(console);

        var tokenHelp = string.Join(
            ' ',
            console.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        await Assert.That(tokenHelp).Contains(GuidedConfigPrompt.CloudflareTokenPage);
        await Assert.That(tokenHelp).Contains("Core permissions:");
        await Assert.That(tokenHelp).Contains("Cloudflare Tunnel → Edit");
        await Assert.That(tokenHelp).Contains("Only if you plan to use Discord to sign in:");
        await Assert.That(tokenHelp).Contains("Workers Scripts → Edit");
        await Assert.That(tokenHelp).Contains("Do not use a token that has every permission.");
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.HostQuestionCloudflare);
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.HostHintCloudflare);
        await Assert.That(console.Output).Contains("Cloudflare looks up your web address");
        await Assert.That(console.Output).Contains("If Cloudflare does not look that name up, use the local network instead.");
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.RoutingTitle);
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.RoutingSeparate);
        await Assert.That(console.Output).Contains("One address keeps the website and the API on app.example.com.");
        await Assert.That(console.Output).DoesNotContain(GuidedConfigPrompt.GoogleClientIdLabel);
        await Assert.That(console.Output).DoesNotContain(token);
        await Assert.That(config.Edge.Cloudflare.Enabled).IsTrue();
        await Assert.That(config.Edge.Cloudflare.ApiToken).IsEqualTo(token);
        await Assert.That(config.Edge.Cloudflare.TunnelName).IsEqualTo("interfold");
        await Assert.That(config.Edge.Cloudflare.Access.Enabled).IsFalse();
        await Assert.That(console.Output).Contains("not published (Cloudflare Tunnel)");
        await Assert.That(console.Output).DoesNotContain(GuidedConfigPrompt.PortsQuestion);
        await Assert.That(config.Api.OAuth.GoogleClientId).IsEmpty();
        await Assert.That(config.Edge.TlsMode).IsEqualTo(EdgeTlsMode.PrivateCa);
        await Assert.That(config.Edge.Routing.Mode).IsEqualTo(EdgeRoutingMode.Path);
        await Assert.That(config.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://app.example.com");

        ConfigPhase.Validate(config);
        await Assert.That(config.Edge.TlsMode).IsEqualTo(EdgeTlsMode.None);
    }

    [Test]
    public async Task CloudflareAccessRequiresEmailAndGoogle()
    {
        var console = NewConsole();
        Accept(console);
        Select(console, downArrows: 1);
        Accept(console);
        Answer(console, "access.example.com");
        Answer(console, "cfat-access-token");
        Answer(console, "y");
        Answer(console, "person@example.com");
        Accept(console);
        Tick(console, downArrows: 0);
        Answer(console, "google-client");
        Answer(console, "google-secret");
        DeclineOptional(console);
        Accept(console);

        var config = Run(console);

        var flat = console.Output.ReplaceLineEndings(" ");
        await Assert.That(flat).Contains("create a new project to hold these credentials");
        await Assert.That(flat).Contains("https://console.cloud.google.com/apis/credentials");
        await Assert.That(flat).Contains("Web application");
        await Assert.That(flat).Contains(".local will not work.");
        await Assert.That(flat).Contains("https://access.example.com/auth/google/callback");
        await Assert.That(flat).Contains("https://access.example.com/auth/link/google/callback");
        await Assert.That(flat).Contains("cloudflareaccess.com/cdn-cgi/access/callback");
        await Assert.That(flat).Contains("example.com allows ada@example.com");
        await Assert.That(flat).Contains("same Google Cloud project you created for Google sign-in");
        await Assert.That(flat).Contains("pick the Google Cloud project you use for sign-in");
        await Assert.That(flat).Contains("Leave out the import lines above it.");
        await Assert.That(flat).Contains("Under Web Push certificates, copy the key pair.");
        await Assert.That(flat).DoesNotContain("type a name for the project");
        await Assert.That(console.Output).Contains("such as example.com");
        await Assert.That(flat).DoesNotContain("discord.com/developers/applications");
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.GoogleClientIdLabel);
        await Assert.That(console.Output).DoesNotContain(GuidedConfigPrompt.DiscordClientIdLabel);
        await Assert.That(config.Edge.Cloudflare.Access.Enabled).IsTrue();
        await Assert.That(config.Edge.Cloudflare.Access.AllowedEmails).IsEquivalentTo(["person@example.com"]);
        await Assert.That(config.Api.OAuth.GoogleClientId).IsEqualTo("google-client");
        await Assert.That(config.Api.OAuth.GoogleClientSecret).IsEqualTo("google-secret");
        await Assert.That(config.Api.OAuth.DiscordClientId).IsEmpty();
        ConfigPhase.Validate(config);
        await Assert.That(config.Edge.TlsMode).IsEqualTo(EdgeTlsMode.None);
    }

    [Test]
    public async Task LanReconfigureFromAccessStillOffersDiscord()
    {
        var existing = new BootstrapConfig();
        existing.Edge.Cloudflare.Enabled = true;
        existing.Edge.Cloudflare.Access.Enabled = true;
        existing.Edge.Hosts = ["interfold.co.uk"];
        existing.Api.OAuth.DiscordClientId = "discord-client";
        existing.Api.OAuth.DiscordClientSecret = "discord-secret";

        var console = NewConsole();
        Accept(console);
        Select(console, downArrows: 1);
        Accept(console);
        Answer(console, "home.local");
        Accept(console);
        Accept(console);
        Accept(console);
        Accept(console);
        Accept(console);
        DeclineOptional(console);
        Accept(console);

        var config = Run(console, existing: existing);

        await Assert.That(console.Output).Contains(GuidedConfigPrompt.SignInTitle);
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.SignInDiscord);
        await Assert.That(config.Edge.Cloudflare.Enabled).IsFalse();
        await Assert.That(config.Edge.Cloudflare.Access.Enabled).IsFalse();
        await Assert.That(config.Api.OAuth.DiscordClientId).IsEqualTo("discord-client");
        await Assert.That(config.Api.OAuth.GoogleClientId).IsEmpty();
    }

    [Test]
    public async Task LocalNameUsesDiscordForAccessAndSkipsGoogle()
    {
        var console = NewConsole();
        Accept(console);
        Select(console, downArrows: 1);
        Accept(console);
        Answer(console, "home.local");
        Answer(console, "cfat-access-token");
        Answer(console, "y");
        Answer(console, "person@example.com");
        Accept(console);
        Accept(console);
        Answer(console, "discord-client");
        Answer(console, "discord-secret");
        DeclineOptional(console);
        Accept(console);

        var config = Run(console);

        await Assert.That(console.Output).Contains("Google sign-in is not available for this address.");
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.AccessUsesDiscord);
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.DiscordClientIdLabel);
        await Assert.That(console.Output).DoesNotContain(GuidedConfigPrompt.GoogleClientIdLabel);
        await Assert.That(config.Edge.Cloudflare.Access.Enabled).IsTrue();
        await Assert.That(config.Api.OAuth.GoogleClientId).IsEmpty();
        await Assert.That(config.Api.OAuth.DiscordClientId).IsEqualTo("discord-client");
        await Assert.That(config.Api.OAuth.DiscordClientSecret).IsEqualTo("discord-secret");
        ConfigPhase.Validate(config);
    }

    [Test]
    public async Task LanPrefillKeepsHostnameBeforeDetectedAddress()
    {
        var console = NewConsole();
        Accept(console);
        Accept(console);
        Accept(console);
        Accept(console);
        Accept(console);
        Accept(console);
        Accept(console);
        DeclineOptional(console);
        Accept(console);

        var config = Run(console, hostname: "box.local", ip: IPAddress.Parse("10.1.2.3"));

        await Assert.That(config.Edge.Hosts).IsEquivalentTo(["box.local", "10.1.2.3"]);
        await Assert.That(config.Edge.Hosts[0]).IsEqualTo("box.local");
    }

    [Test]
    public async Task CloudflarePrefillDropsDetectedIp()
    {
        var console = NewConsole();
        Accept(console);
        Select(console, downArrows: 1);
        Accept(console);
        Accept(console);
        Answer(console, "cfat-prefill-token");
        Accept(console);
        Accept(console);
        DeclineOptional(console);
        Accept(console);

        var config = Run(console, hostname: "box.local", ip: IPAddress.Parse("10.1.2.3"));

        await Assert.That(config.Edge.Hosts).IsEquivalentTo(["box.local"]);
    }

    [Test]
    public async Task SummaryAdvancedOpensEditorSeededFromGuidedAnswers()
    {
        var console = NewConsole();
        Accept(console);
        Accept(console);
        Accept(console);
        Answer(console, "lan.example.com");
        Accept(console);
        Accept(console);
        Accept(console);
        DeclineOptional(console);
        Select(console, downArrows: 2);
        ConfirmAdvanced(console);

        var config = Run(console);

        await Assert.That(console.Output).Contains("Configure interfold.bootstrap.json");
        await Assert.That(config.Edge.Hosts).IsEquivalentTo(["lan.example.com"]);
        await Assert.That(config.Edge.TlsMode).IsEqualTo(EdgeTlsMode.PrivateCa);
        await Assert.That(config.Edge.Certificates.TrustStoreInstall).IsTrue();
        await Assert.That(config.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://lan.example.com");
    }

    [Test]
    public async Task UpdatesYesWithoutBootstrapperAppliesSafePreset()
    {
        var console = NewConsole();
        PushThroughSignIn(console);
        Answer(console, "y");
        Accept(console);
        Accept(console);
        Answer(console, "y");
        Accept(console);
        Accept(console);
        Accept(console);
        Accept(console);

        var config = Run(console);

        await Assert.That(config.Deployment.Backup.Enabled).IsTrue();
        await Assert.That(config.Deployment.Backup.Schedule).IsEqualTo("daily");
        await Assert.That(config.Deployment.Backup.RetainCount).IsEqualTo(14);
        await Assert.That(config.Deployment.Backup.Directory).IsEmpty();
        await Assert.That(config.Deployment.Update.Enabled).IsTrue();
        await Assert.That(config.Deployment.Update.RecreateOnUpdate).IsTrue();
        await Assert.That(config.Deployment.Update.AutoRestoreOnFailure).IsTrue();
        await Assert.That(config.Deployment.Update.HealthCheckTimeoutSeconds).IsEqualTo(180);
        await Assert.That(config.Deployment.Update.Services).IsEmpty();
        await Assert.That(config.Deployment.Update.Bootstrapper.Enabled).IsFalse();
        await Assert.That(config.Deployment.Update.Bootstrapper.AutoRollbackOnFailure).IsFalse();
        await Assert.That(config.Deployment.Update.Bootstrapper.Channel).IsEqualTo(BootstrapperReleaseChannel.Stable);
        await Assert.That(config.Deployment.Update.Bootstrapper.UpdateOnBootstrap).IsNull();
        await Assert.That(console.Output).Contains("on, bootstrapper not updated");
        ConfigPhase.Validate(config);
    }

    [Test]
    public async Task UpdatesYesWithBootstrapperEnablesRollback()
    {
        var console = NewConsole();
        PushThroughSignIn(console);
        Answer(console, "y");
        Accept(console);
        Accept(console);
        Answer(console, "y");
        Answer(console, "y");
        Accept(console);
        Accept(console);
        Accept(console);

        var config = Run(console);

        await Assert.That(config.Deployment.Update.Enabled).IsTrue();
        await Assert.That(config.Deployment.Update.Bootstrapper.Enabled).IsTrue();
        await Assert.That(config.Deployment.Update.Bootstrapper.AutoRollbackOnFailure).IsTrue();
        await Assert.That(config.Deployment.Update.Bootstrapper.UpdateOnBootstrap).IsNull();
        await Assert.That(config.Deployment.Update.Bootstrapper.Channel).IsEqualTo(BootstrapperReleaseChannel.Stable);
        await Assert.That(console.Output).Contains("bootstrapper updated, rollback on failure");
    }

    [Test]
    public async Task UpdatesNoLeavesUpdateDisabledAndSkipsBootstrapperQuestion()
    {
        var console = NewConsole();
        PushThroughSignIn(console);
        Answer(console, "y");
        Accept(console);
        Accept(console);
        Accept(console);
        Accept(console);
        Accept(console);
        Accept(console);

        var config = Run(console);

        await Assert.That(config.Deployment.Backup.Enabled).IsTrue();
        await Assert.That(config.Deployment.Update.Enabled).IsFalse();
        await Assert.That(config.Deployment.Update.AutoRestoreOnFailure).IsFalse();
        await Assert.That(console.Output).Contains(GuidedConfigPrompt.UpdateQuestion);
        await Assert.That(console.Output).DoesNotContain(GuidedConfigPrompt.BootstrapperUpdateQuestion);
        await Assert.That(console.Output).Contains("off");
    }

    [Test]
    public async Task FirebaseAutoDetectFillsPathsFromFolder()
    {
        using var scratch = TestSupport.NewScratchDir("guided-firebase");
        var folder = scratch.Path;
        var android = Path.Combine(folder, "google-services.json");
        var ios = Path.Combine(folder, "GoogleService-Info.plist");
        var web = Path.Combine(folder, "firebase-web-config.json");
        var sa = Path.Combine(folder, "octocon-firebase-adminsdk-abc.json");
        File.WriteAllText(android, "{}");
        File.WriteAllText(ios, "<plist/>");
        File.WriteAllText(web, "{}");
        File.WriteAllText(sa, """{"type":"service_account"}""");

        var console = NewConsole();
        PushThroughSignIn(console);
        Accept(console);
        Answer(console, "y");
        Accept(console);
        Answer(console, folder);
        Accept(console);
        Accept(console);

        var config = Run(console);

        await Assert.That(config.Api.Firebase.AndroidConfigPath).IsEqualTo(Path.GetFullPath(android));
        await Assert.That(config.Api.Firebase.IosConfigPath).IsEqualTo(Path.GetFullPath(ios));
        await Assert.That(config.Api.Firebase.WebConfigPath).IsEqualTo(Path.GetFullPath(web));
        await Assert.That(config.Api.Firebase.ServiceAccountPath).IsEqualTo(Path.GetFullPath(sa));
        await Assert.That(console.Output).Contains("4/4 configured");
    }

    [Test]
    public async Task OtlpAdvertiseOffSkipsClientOverride()
    {
        var console = NewConsole();
        PushThroughSignIn(console);
        Accept(console);
        Accept(console);
        Answer(console, "y");
        Answer(console, "https://otel.example.com/v1/traces");
        Accept(console);
        Accept(console);

        var config = Run(console);

        await Assert.That(config.Observability.OtlpEndpoint).IsEqualTo("https://otel.example.com/v1/traces");
        await Assert.That(config.Observability.AdvertiseOtlpToClients).IsFalse();
        await Assert.That(config.Observability.ClientOtlpHttpEndpoint).IsEmpty();
        await Assert.That(console.Output).Contains("or a different one.");
        await Assert.That(console.Output).DoesNotContain(GuidedConfigPrompt.OtlpClientOverrideLabel);
        await Assert.That(console.Output).Contains("not advertised");
        ConfigPhase.Validate(config);
    }

    [Test]
    public async Task OtlpAdvertiseOnRecordsClientOverride()
    {
        var console = NewConsole();
        PushThroughSignIn(console);
        Accept(console);
        Accept(console);
        Answer(console, "y");
        Answer(console, "https://otel.example.com/v1/traces");
        Answer(console, "y");
        Answer(console, "https://clients.example.com/v1/traces");
        Accept(console);

        var config = Run(console);

        await Assert.That(config.Observability.AdvertiseOtlpToClients).IsTrue();
        await Assert.That(config.Observability.ClientOtlpHttpEndpoint).IsEqualTo("https://clients.example.com/v1/traces");
        await Assert.That(console.Output).Contains("advertised to clients");
        ConfigPhase.Validate(config);
    }

    [Test]
    public async Task LanCanPublishNonStandardPorts()
    {
        var console = NewConsole();
        Accept(console);
        Accept(console);
        Accept(console);
        Answer(console, "192.168.1.42");
        Accept(console);
        Answer(console, "n");
        Answer(console, "8080");
        Answer(console, "8443");
        Accept(console);
        DeclineOptional(console);
        Accept(console);

        var config = Run(console);

        await Assert.That(config.Edge.Ports.Http).IsEqualTo(8080);
        await Assert.That(config.Edge.Ports.Https).IsEqualTo(8443);
        await Assert.That(config.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://192.168.1.42:8443");
        await Assert.That(console.Output).Contains("8080 and 8443");
        ConfigPhase.Validate(config);
    }

    [Test]
    public async Task AppleSignInRecordsClientCredentials()
    {
        var console = NewConsole();
        Accept(console);
        Accept(console);
        Accept(console);
        Answer(console, "192.168.1.42");
        Accept(console);
        Accept(console);
        Tick(console, downArrows: 1);
        Answer(console, "com.example.interfold");
        Answer(console, "apple-secret");
        DeclineOptional(console);
        Accept(console);

        var config = Run(console);

        await Assert.That(config.Api.OAuth.AppleClientId).IsEqualTo("com.example.interfold");
        await Assert.That(config.Api.OAuth.AppleClientSecret).IsEqualTo("apple-secret");
        await Assert.That(config.Api.OAuth.GoogleClientId).IsEmpty();
        var flat = console.Output.ReplaceLineEndings(" ");
        await Assert.That(flat).Contains("Services ID is the client ID");
        await Assert.That(flat).Contains("https://192.168.1.42/auth/apple/callback");
        await Assert.That(flat).Contains("https://192.168.1.42/auth/link/apple/callback");
        await Assert.That(flat).Contains("at most six months");
        await Assert.That(console.Output).DoesNotContain("apple-secret");
        await Assert.That(console.Output).Contains("Apple (set)");
    }

    private static void PushThroughSignIn(TestConsole console)
    {
        Accept(console);
        Accept(console);
        Accept(console);
        Answer(console, "192.168.1.42");
        Accept(console);
        Accept(console);
        Accept(console);
    }
}
