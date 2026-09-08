using System.Net;
using System.Text.RegularExpressions;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Shared.Contracts.Enums;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>Drives Spectre <c>SelectionPrompt&lt;int&gt;</c> for the config form: 54 field
/// rows across 10 sections + trailing "Confirm and save". Headers are inert; cursor resets
/// to field 0 after each edit, so tests use absolute Navigate distances.
/// <para>Field order: 0..2 Deployment · 3..15 Edge · 16..19 Datastores · 20..26 API ·
/// 27..28 Storage · 29..33 Performance · 34..39 OAuth · 40..43 Backup · 44..52 Updates ·
/// 53 Firebase. Navigate(54) = Confirm and save.</para>
/// <para><c>[NotInParallel("bootstrapper-console")]</c>: Spectre TestConsole + MTP's
/// NamedPipeServer race under Linux thread pressure (dotnet/runtime#58045). ~10s serialised.
/// <c>[Retry(2)]</c> absorbs the residual native abort (exit 134/SIGABRT) that still slips
/// through on 2-core GHA runners when the pipe race wins — the underlying runtime bug is
/// non-deterministic, and the test logic itself is race-free, so a retry is safe.</para></summary>
[NotInParallel("bootstrapper-console")]
[Retry(2)]
public sealed class ConfigInteractivePromptTests
{
    private const int FieldCount = 54;

    /// <summary>Sized to fit the whole 60-row form so Spectre never paginates.</summary>
    private static TestConsole NewConsole()
    {
        var c = new TestConsole();
        c.Interactive();
        c.Profile.Height = 120;
        c.Profile.Width = 130;
        return c;
    }

    /// <summary>Pushes N DownArrows + Enter. Field N sits N DownArrows below field 0.</summary>
    private static void Navigate(TestConsole c, int downArrows)
    {
        for (var i = 0; i < downArrows; i++)
            c.Input.PushKey(ConsoleKey.DownArrow);
        c.Input.PushKey(ConsoleKey.Enter);
    }

    private static void ConfirmForm(TestConsole c) => Navigate(c, FieldCount);

    /// <summary>Opens the field's editor and pushes <paramref name="answers"/> as lines
    /// (multiple answers drive a re-prompt on invalid input).</summary>
    private static void EditField(TestConsole c, int fieldIndex, params string[] answers)
    {
        Navigate(c, fieldIndex);
        foreach (var a in answers)
            c.Input.PushTextWithEnter(a);
    }

    /// <summary>
    /// Standard wrapper — both probes return null so neither auto-default fires in tests. A
    /// CI runner with a routable NIC would otherwise pre-populate Hosts and break the empty-
    /// Hosts assertions. Tests exercising the auto-default call <c>PromptForConfig</c> directly.
    /// </summary>
    private static BootstrapConfig PromptWithoutDetection(IAnsiConsole console, bool maskSecrets = false) =>
        ConfigPhase.PromptForConfig(
            console,
            maskSecrets,
            localAddressProbe: () => null,
            hostnameProbe: () => null);

    [Test]
    public async Task ConfirmingFormImmediatelyUsesDefaults()
    {
        var console = NewConsole();
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        // Deployment defaults
        await Assert.That(config.Deployment.OutputDir).IsEqualTo("./deploy");
        await Assert.That(config.Edge.Certificates.RootCaName).IsEqualTo("Interfold Root CA");
        await Assert.That(config.Edge.Certificates.CertYears).IsEqualTo(5);
        await Assert.That(config.Edge.Certificates.TrustStoreInstall).IsTrue();
        await Assert.That(config.Deployment.IncludeWeb).IsFalse();
        await Assert.That(config.Edge.TlsMode).IsEqualTo(EdgeTlsMode.PrivateCa);
        await Assert.That(config.Edge.Hosts.Count).IsEqualTo(0);

        await Assert.That(config.Edge.Ports.Http).IsEqualTo(80);
        await Assert.That(config.Edge.Ports.Https).IsEqualTo(443);

        // Datastores / API defaults
        await Assert.That(config.Datastores.Cql.Backend).IsEqualTo(CqlBackend.ScyllaSingle);
        await Assert.That(config.Datastores.Postgres.Database).IsEqualTo("interfold");
        await Assert.That(config.Datastores.Cql.ClusterName).IsEqualTo("InterfoldCluster");
        await Assert.That(config.Datastores.Cql.Keyspace).IsEqualTo(ScyllaKeyspace.Nam);
        await Assert.That(config.Api.Image).IsEqualTo("ghcr.io/azyyyyyy/interfold-api:latest");

        // ApiRuntime: derivation happens in RunAsync / Validate, not PromptForConfig, so
        // the stored fields stay empty here (the menu's Show callbacks derive for display only).
        await Assert.That(config.Api.OAuth.CallbackBaseUrl).IsEqualTo(string.Empty);
        await Assert.That(config.Api.OAuth.JwtAuthority).IsEqualTo(string.Empty);
        await Assert.That(config.Api.OAuth.JwtAudience).IsEqualTo("octocon");
        await Assert.That(config.Api.CorsAllowedOrigins.Count).IsEqualTo(0);

        // Blank string/null fields reproduce the pre-bootstrapper "env var unset" behaviour.
        await Assert.That(config.Api.NodeGroup).IsEqualTo(NodeGroup.Auxiliary);
        await Assert.That(config.Observability.OtlpEndpoint).IsEqualTo(string.Empty);
        await Assert.That(config.Api.Storage.AvatarStorageRoot).IsEqualTo(string.Empty);
        await Assert.That(config.Api.Storage.AvatarPublicBase).IsEqualTo(string.Empty);
        await Assert.That(config.Api.BatchBytesThreshold).IsNull();
        await Assert.That(config.Api.Resilience.DbRetryAttempts).IsEqualTo(3);
        await Assert.That(config.Api.Resilience.DbRetryInitialDelayMs).IsEqualTo(100);
        await Assert.That(config.Api.Resilience.DbRetryMaxDelayMs).IsEqualTo(1500);
        await Assert.That(config.Api.Resilience.HydrationMaxConcurrency).IsEqualTo(8);

        // OAuth credentials default to empty (paired per provider: ID then secret).
        await Assert.That(config.Api.OAuth.GoogleClientId).IsEqualTo(string.Empty);
        await Assert.That(config.Api.OAuth.GoogleClientSecret).IsEqualTo(string.Empty);
        await Assert.That(config.Api.OAuth.DiscordClientId).IsEqualTo(string.Empty);
        await Assert.That(config.Api.OAuth.DiscordClientSecret).IsEqualTo(string.Empty);
        await Assert.That(config.Api.OAuth.AppleClientId).IsEqualTo(string.Empty);
        await Assert.That(config.Api.OAuth.AppleClientSecret).IsEqualTo(string.Empty);

        // Update section defaults to "manual only" — stock bootstrap matches pre-feature deployments.
        await Assert.That(config.Deployment.Update.Enabled).IsFalse();
        await Assert.That(config.Deployment.Update.HealthCheckTimeoutSeconds).IsEqualTo(180);
        await Assert.That(config.Deployment.Update.AutoRestoreOnFailure).IsFalse();
        await Assert.That(config.Deployment.Update.RecreateOnUpdate).IsTrue();
        await Assert.That(config.Deployment.Update.Services.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EditingHostsParsesCommaSeparated()
    {
        var console = NewConsole();
        EditField(console, fieldIndex: 3, "api.example.com,admin.example.com,www.example.com");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Edge.Hosts.Count).IsEqualTo(3);
        await Assert.That(config.Edge.Hosts).Contains("api.example.com");
        await Assert.That(config.Edge.Hosts).Contains("admin.example.com");
        await Assert.That(config.Edge.Hosts).Contains("www.example.com");
    }

    [Test]
    public async Task EditingHostsTrimsWhitespace()
    {
        var console = NewConsole();
        EditField(console, fieldIndex: 3, "  foo.example.com  ,  bar.example.com  ");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Edge.Hosts.Count).IsEqualTo(2);
        await Assert.That(config.Edge.Hosts).Contains("foo.example.com");
        await Assert.That(config.Edge.Hosts).Contains("bar.example.com");
    }

    [Test]
    public async Task EditingHostsAcceptsIpAndCidr()
    {
        // IPv4 literal + IPv6 CIDR must reach the stored list verbatim.
        var console = NewConsole();
        EditField(console, fieldIndex: 3, "192.168.1.42,fe80::/64");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Edge.Hosts.Count).IsEqualTo(2);
        await Assert.That(config.Edge.Hosts[0]).IsEqualTo("192.168.1.42");
        await Assert.That(config.Edge.Hosts[1]).IsEqualTo("fe80::/64");
    }

    [Test]
    public async Task DetectedDeviceIpPreFillsHostsOnFreshBootstrap()
    {
        // Deterministic probe (test doesn't depend on the runner's NICs).
        var console = NewConsole();
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => IPAddress.Parse("192.168.1.42"));

        await Assert.That(config.Edge.Hosts.Count).IsEqualTo(1);
        await Assert.That(config.Edge.Hosts[0]).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task DetectedIpv6IsStoredAsBareLiteralNotBracketed()
    {
        // Downstream consumers (SAN encoder, URL derivation) expect the bare literal;
        // storing "[::1]" would round-trip into the JSON with brackets and surprise editors.
        var console = NewConsole();
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => IPAddress.Parse("2001:db8::1"));

        await Assert.That(config.Edge.Hosts.Count).IsEqualTo(1);
        await Assert.That(config.Edge.Hosts[0]).IsEqualTo("2001:db8::1");
    }

    [Test]
    public async Task OperatorEditedHostsOverrideDetectedIpDefault()
    {
        // Auto-default is just a pre-fill — a typed value must win.
        var console = NewConsole();
        EditField(console, fieldIndex: 3, "api.example.com");
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => IPAddress.Parse("192.168.1.42"));

        await Assert.That(config.Edge.Hosts.Count).IsEqualTo(1);
        await Assert.That(config.Edge.Hosts[0]).IsEqualTo("api.example.com");
    }

    [Test]
    public async Task NullDetectorLeavesHostsEmpty()
    {
        // Null detector must NOT invent a fallback (would mask a future "default to 127.0.0.1"
        // regression). Validate fails fast on the resulting empty Hosts downstream.
        var console = NewConsole();
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => null);

        await Assert.That(config.Edge.Hosts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DetectedIpAppearsInPublicHostMenuRow()
    {
        // Guards against a refactor that pre-fills Hosts but doesn't render the value on its row.
        var console = NewConsole();
        ConfirmForm(console);

        ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => IPAddress.Parse("10.0.0.42"));

        await Assert.That(Regex.IsMatch(console.Output, @"Public host\(s\)\s+10\.0\.0\.42")).IsTrue();
    }

    [Test]
    public async Task HostsPrefillIncludesMdnsHostnameAndIp()
    {
        // Order matters: hostname first so HostParser.PickPrimary latches it for leaf-cert
        // and derived-URL purposes. A swap would silently point every derived URL at the IP.
        var console = NewConsole();
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => IPAddress.Parse("192.168.1.42"),
            hostnameProbe: () => "workstation.local");

        await Assert.That(config.Edge.Hosts.Count).IsEqualTo(2);
        await Assert.That(config.Edge.Hosts[0]).IsEqualTo("workstation.local");
        await Assert.That(config.Edge.Hosts[1]).IsEqualTo("192.168.1.42");
        // Menu row = comma-joined; pin the exact display so a "print only Hosts[0]" regression gets caught.
        await Assert.That(Regex.IsMatch(console.Output, @"Public host\(s\)\s+workstation\.local,192\.168\.1\.42")).IsTrue();
    }

    [Test]
    public async Task HostsPrefillOmitsHostnameWhenProbeReturnsNull()
    {
        // Null hostname probe (banner reported mDNS unavailable) must not become an empty
        // or null entry in Hosts — only the detected IP lands.
        var console = NewConsole();
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => IPAddress.Parse("192.168.1.42"),
            hostnameProbe: () => null);

        await Assert.That(config.Edge.Hosts.Count).IsEqualTo(1);
        await Assert.That(config.Edge.Hosts[0]).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task EditingOAuthSecretsCapturesValues()
    {
        // Paired per provider (ID then secret): secrets sit at 34 / 36 / 38.
        var console = NewConsole();
        EditField(console, fieldIndex: 35, "google-secret-xyz");
        EditField(console, fieldIndex: 37, "discord-secret-abc");
        EditField(console, fieldIndex: 39, "apple-secret-jwt");
        ConfirmForm(console);

        // maskSecrets:false keeps the prompt off the ReadKey path so PushTextWithEnter suffices.
        var config = PromptWithoutDetection(console);

        await Assert.That(config.Api.OAuth.GoogleClientSecret).IsEqualTo("google-secret-xyz");
        await Assert.That(config.Api.OAuth.DiscordClientSecret).IsEqualTo("discord-secret-abc");
        await Assert.That(config.Api.OAuth.AppleClientSecret).IsEqualTo("apple-secret-jwt");
    }

    [Test]
    public async Task EditingOAuthClientIdsCapturesValues()
    {
        // Public IDs → plain PromptStr (no masking). ID rows sit at 33 / 35 / 37.
        var console = NewConsole();
        EditField(console, fieldIndex: 34, "1234.apps.googleusercontent.com");
        EditField(console, fieldIndex: 36, "9876543210");
        EditField(console, fieldIndex: 38, "com.example.interfold.signin");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Api.OAuth.GoogleClientId).IsEqualTo("1234.apps.googleusercontent.com");
        await Assert.That(config.Api.OAuth.DiscordClientId).IsEqualTo("9876543210");
        await Assert.That(config.Api.OAuth.AppleClientId).IsEqualTo("com.example.interfold.signin");
    }

    [Test]
    public async Task FormShowsEmptyMarkerNextToUnsetOAuthClientIds()
    {
        // Parity with secret rows: an unset ID must render <empty>, not a blank cell (which
        // reads as "no row" and can lead operators to miss the second half of a provider's pair).
        var console = NewConsole();
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        await Assert.That(Regex.IsMatch(output, @"Google OAuth client ID\s+<empty>")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"Discord OAuth client ID\s+<empty>")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"Apple OAuth client ID\s+<empty>")).IsTrue();
    }

    [Test]
    public async Task FormShowsOAuthClientIdsVerbatimInMenuRow()
    {
        // IDs are public → the menu row echoes the value verbatim, not <set>/<empty>.
        const string googleId = "1234.apps.googleusercontent.com";
        var console = NewConsole();
        EditField(console, fieldIndex: 34, googleId);
        ConfirmForm(console);

        PromptWithoutDetection(console);

        await Assert.That(console.Output).Contains(googleId);
    }

    [Test]
    public async Task EditingPortsCapturesOverrides()
    {
        var console = NewConsole();
        EditField(console, fieldIndex: 14, "8088");   // EdgeHttp
        EditField(console, fieldIndex: 15, "8443");   // EdgeHttps
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Edge.Ports.Http).IsEqualTo(8088);
        await Assert.That(config.Edge.Ports.Https).IsEqualTo(8443);
    }

    [Test]
    public async Task EditingBoolsCapturesOverrides()
    {
        var console = NewConsole();
        EditField(console, fieldIndex: 6, "n");   // TrustStoreInstall := false
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Edge.Certificates.TrustStoreInstall).IsFalse();
    }

    [Test]
    public async Task EditingIncludeWebCapturesOverride()
    {
        // IncludeWeb (row 1) toggles independently from edge TLS settings.
        var console = NewConsole();
        EditField(console, fieldIndex: 1, "y");   // IncludeWeb := true
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.IncludeWeb).IsTrue();
    }

    [Test]
    public async Task PortPromptRePromptsOnInvalidInt()
    {
        // "bad" triggers the re-prompt; the second answer (5005) is what should stick.
        var console = NewConsole();
        EditField(console, fieldIndex: 14, "bad", "5005");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Edge.Ports.Http).IsEqualTo(5005);
        await Assert.That(console.Output).Contains("must be an integer");
    }

    [Test]
    public async Task BoolPromptRePromptsOnInvalidAnswer()
    {
        var console = NewConsole();
        EditField(console, fieldIndex: 6, "maybe", "n");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Edge.Certificates.TrustStoreInstall).IsFalse();
    }

    [Test]
    public async Task CqlBackendPromptEnforcesChoices()
    {
        var console = NewConsole();
        EditField(console, fieldIndex: 16, "invalid", "scylla-multi");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Datastores.Cql.Backend).IsEqualTo(CqlBackend.ScyllaMulti);
    }

    [Test]
    public async Task EditingScyllaKeyspaceCapturesValue()
    {
        // Happy-path edit; AddChoices enforcement is covered by the rejection test below.
        var console = NewConsole();
        EditField(console, fieldIndex: 19, "eur");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Datastores.Cql.Keyspace).IsEqualTo(ScyllaKeyspace.Eur);
    }

    [Test]
    public async Task ScyllaKeyspacePromptEnforcesChoices()
    {
        // AddChoices re-prompts on non-listed values; the eventually-accepted value sticks.
        var console = NewConsole();
        EditField(console, fieldIndex: 19, "ant", "gdpr");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Datastores.Cql.Keyspace).IsEqualTo(ScyllaKeyspace.Gdpr);
    }

    [Test]
    public async Task EditingApiRuntimeCapturesValues()
    {
        // Rows 19-22: CallbackBaseUrl / JwtAuthority / JwtAudience / CORS. Verbatim round-trip.
        var console = NewConsole();
        EditField(console, fieldIndex: 20, "https://api.custom.example.com");
        EditField(console, fieldIndex: 21, "https://issuer.custom.example.com");
        EditField(console, fieldIndex: 22, "custom-aud");
        EditField(console, fieldIndex: 23, "https://app.example.com,https://admin.example.com");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://api.custom.example.com");
        await Assert.That(config.Api.OAuth.JwtAuthority).IsEqualTo("https://issuer.custom.example.com");
        await Assert.That(config.Api.OAuth.JwtAudience).IsEqualTo("custom-aud");
        await Assert.That(config.Api.CorsAllowedOrigins.Count).IsEqualTo(2);
        await Assert.That(config.Api.CorsAllowedOrigins).Contains("https://app.example.com");
        await Assert.That(config.Api.CorsAllowedOrigins).Contains("https://admin.example.com");
    }

    [Test]
    public async Task FormShowsDerivedCallbackBaseUrlInMenuRow()
    {
        // ApiRuntime Show callbacks paint the derived default when the stored field is empty.
        var console = NewConsole();
        EditField(console, fieldIndex: 3, "api.example.com");
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        await Assert.That(Regex.IsMatch(output, @"OAuth callback base URL\s+https://api\.example\.com")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"JWT authority \(iss claim\)\s+https://api\.example\.com")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"CORS allowed origins\s+https://api\.example\.com")).IsTrue();
    }

    [Test]
    public async Task CorsAllowedOriginsRePromptsOnInvalidUri()
    {
        // Bare hostnames aren't absolute http(s) URIs — re-prompt; second answer sticks.
        var console = NewConsole();
        EditField(console, fieldIndex: 23,
            "not-a-url,still-not-a-url",
            "https://app.example.com,https://admin.example.com");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Api.CorsAllowedOrigins.Count).IsEqualTo(2);
        await Assert.That(config.Api.CorsAllowedOrigins).Contains("https://app.example.com");
        await Assert.That(console.Output).Contains("not a valid http(s) origin");
    }

    [Test]
    public async Task FormListsEveryFieldLabel()
    {
        // Confirming immediately is enough — the menu renders before the first key is consumed.
        var console = NewConsole();
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        // Deployment
        await Assert.That(output).Contains("Output directory");
        await Assert.That(output).Contains("Public host");
        await Assert.That(output).Contains("Root CA");
        await Assert.That(output).Contains("Leaf cert validity");
        await Assert.That(output).Contains("Install root CA");
        await Assert.That(output).Contains("octocon-web");
        await Assert.That(output).Contains("Edge TLS mode");
        await Assert.That(output).Contains("Edge routing");
        await Assert.That(output).Contains("Cloudflare Tunnel");
        await Assert.That(output).Contains("Edge HTTP port");
        await Assert.That(output).Contains("Edge HTTPS port");
        // Datastores
        await Assert.That(output).Contains("CQL backend");
        await Assert.That(output).Contains("Postgres application DB name");
        await Assert.That(output).Contains("Cluster name");
        await Assert.That(output).Contains("Scylla keyspace (region)");
        // API
        await Assert.That(output).Contains("OAuth callback base URL");
        await Assert.That(output).Contains("JWT authority (iss claim)");
        await Assert.That(output).Contains("JWT audience (aud claim)");
        await Assert.That(output).Contains("CORS allowed origins");
        await Assert.That(output).Contains("API image");
        await Assert.That(output).Contains("Cluster node group");
        await Assert.That(output).Contains("OTLP endpoint");
        // Storage
        await Assert.That(output).Contains("Avatar storage root");
        await Assert.That(output).Contains("Avatar public base URL");
        // Performance tuning
        await Assert.That(output).Contains("Socket batch flush threshold");
        await Assert.That(output).Contains("DB retry attempts");
        await Assert.That(output).Contains("DB retry initial delay");
        await Assert.That(output).Contains("DB retry max delay");
        await Assert.That(output).Contains("Hydration max concurrency");
        // OAuth: assert ID and secret rows separately so a dropped label is caught.
        await Assert.That(output).Contains("Google OAuth client ID");
        await Assert.That(output).Contains("Google OAuth client secret");
        await Assert.That(output).Contains("Discord OAuth client ID");
        await Assert.That(output).Contains("Discord OAuth client secret");
        await Assert.That(output).Contains("Apple OAuth client ID");
        await Assert.That(output).Contains("Apple OAuth client secret");
    }

    [Test]
    public async Task FormListsEverySectionHeader()
    {
        var console = NewConsole();
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        await Assert.That(Regex.IsMatch(output, @"---\s+Deployment\s+---")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"---\s+Edge\s+---")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"---\s+Datastores\s+---")).IsTrue();
        // "---" framing distinguishes ambiguous tokens (e.g. "API" also appears in field labels).
        await Assert.That(Regex.IsMatch(output, @"---\s+API\s+---")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"---\s+Storage\s+---")).IsTrue();
        await Assert.That(output).Contains("Performance tuning");
        await Assert.That(output).Contains("OAuth credentials");
        await Assert.That(Regex.IsMatch(output, @"---\s+Backup\s+---")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"---\s+Updates\s+---")).IsTrue();
        await Assert.That(output).Contains("Firebase");
        await Assert.That(output).Contains("Configure interfold.bootstrap.json");
        await Assert.That(output).Contains("Confirm and save");
    }

    [Test]
    public async Task SectionHeadersSitAboveTheirFirstField()
    {
        // Position guard (FormListsEverySectionHeader only checks presence). The grouped
        // declaration makes mechanical index drift impossible, so this now catches a field
        // declared under the wrong Group(...) — same visible symptom.
        // Assert: previous section's last field < header < current section's first field.
        var console = NewConsole();
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        // (previous section's last field label, header text, section's first field label)
        var boundaries = new (string PrevField, string Header, string FirstField)[]
        {
            ("Autostart server on boot",                 "--- Edge ---",               "Public host(s)"),
            ("Edge HTTPS port",                          "--- Datastores ---",         "CQL backend"),
            ("Scylla keyspace (region)",                 "--- API ---",                "OAuth callback base URL"),
            ("OTLP endpoint",                            "--- Storage ---",            "Avatar storage root"),
            ("Avatar public base URL",                   "--- Performance tuning ---", "Socket batch flush threshold"),
            ("Hydration max concurrency",                "--- OAuth credentials ---",  "Google OAuth client ID"),
            ("Apple OAuth client secret",                "--- Backup ---",             "Scheduled backups enabled"),
            ("Backup directory (absolute, blank=default)", "--- Updates ---",          "Chain updates after backup"),
            ("Update service whitelist (blank=all)",     "--- Firebase ---",           "Firebase push notifications"),
        };
        foreach (var (prevField, header, firstField) in boundaries)
        {
            var prevIdx = output.IndexOf(prevField, StringComparison.Ordinal);
            var headerIdx = output.IndexOf(header, StringComparison.Ordinal);
            var firstIdx = output.IndexOf(firstField, StringComparison.Ordinal);
            await Assert.That(prevIdx).IsGreaterThanOrEqualTo(0);
            await Assert.That(headerIdx).IsGreaterThan(prevIdx)
                .Because($"'{header}' must render below '{prevField}'");
            await Assert.That(firstIdx).IsGreaterThan(headerIdx)
                .Because($"'{firstField}' must render below '{header}'");
        }
    }

    [Test]
    public async Task EditingBackupTogglesAndSchedule()
    {
        // Happy-path edit of every row in the four-row backup section (39..42).
        var console = NewConsole();
        EditField(console, fieldIndex: 40, "y");                           // Enabled := true
        EditField(console, fieldIndex: 41, "Mon..Fri 03:30");              // Schedule
        EditField(console, fieldIndex: 42, "30");                          // RetainCount
        EditField(console, fieldIndex: 43, "/srv/backups/interfold");     // Directory
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.Backup.Enabled).IsTrue();
        await Assert.That(config.Deployment.Backup.Schedule).IsEqualTo("Mon..Fri 03:30");
        await Assert.That(config.Deployment.Backup.RetainCount).IsEqualTo(30);
        await Assert.That(config.Deployment.Backup.Directory).IsEqualTo("/srv/backups/interfold");
    }

    [Test]
    public async Task BackupRetainPromptRejectsOutOfRangeValues()
    {
        // 0 is outside [1..1000]; PromptInt re-prompts and the second answer sticks.
        var console = NewConsole();
        EditField(console, fieldIndex: 42, "0", "7");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.Backup.RetainCount).IsEqualTo(7);
    }

    [Test]
    public async Task BackupSectionEmptyDirectoryDefaults()
    {
        // Empty Directory = the "use {outputDir}/backups" sentinel — operators who ignore
        // the section entirely (the common case) still produce a valid config.
        var console = NewConsole();
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.Backup.Directory).IsEqualTo(string.Empty);
        await Assert.That(config.Deployment.Backup.Enabled).IsFalse();
        await Assert.That(config.Deployment.AutostartServer).IsFalse();
    }

    [Test]
    public async Task EditingNodeGroupCapturesValue()
    {
        // Happy-path edit; rejection covered by NodeGroupPromptEnforcesChoices below.
        var console = NewConsole();
        EditField(console, fieldIndex: 25, "primary");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Api.NodeGroup).IsEqualTo(NodeGroup.Primary);
    }

    [Test]
    public async Task NodeGroupPromptEnforcesChoices()
    {
        // AddChoices re-prompts on non-listed values (same shape as ScyllaKeyspace).
        var console = NewConsole();
        EditField(console, fieldIndex: 25, "guardian", "sidecar");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Api.NodeGroup).IsEqualTo(NodeGroup.Sidecar);
    }

    [Test]
    public async Task EditingStorageAndObservabilityCapturesValues()
    {
        // Non-empty round-trip; blank-state pinned by ConfirmingFormImmediatelyUsesDefaults.
        var console = NewConsole();
        EditField(console, fieldIndex: 26, "http://otel-collector:4317");
        EditField(console, fieldIndex: 27, "/var/lib/interfold/avatars");
        EditField(console, fieldIndex: 28, "https://cdn.example.com/avatars/");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Observability.OtlpEndpoint).IsEqualTo("http://otel-collector:4317");
        await Assert.That(config.Api.Storage.AvatarStorageRoot).IsEqualTo("/var/lib/interfold/avatars");
        await Assert.That(config.Api.Storage.AvatarPublicBase).IsEqualTo("https://cdn.example.com/avatars/");
    }

    [Test]
    public async Task EditingTuningIntsCapturesValues()
    {
        // Row 28 uses PromptNullableInt; the other four use PromptInt. All five round-trip.
        var console = NewConsole();
        EditField(console, fieldIndex: 29, "131072");
        EditField(console, fieldIndex: 30, "5");
        EditField(console, fieldIndex: 31, "250");
        EditField(console, fieldIndex: 32, "3000");
        EditField(console, fieldIndex: 33, "16");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Api.BatchBytesThreshold).IsEqualTo(131072);
        await Assert.That(config.Api.Resilience.DbRetryAttempts).IsEqualTo(5);
        await Assert.That(config.Api.Resilience.DbRetryInitialDelayMs).IsEqualTo(250);
        await Assert.That(config.Api.Resilience.DbRetryMaxDelayMs).IsEqualTo(3000);
        await Assert.That(config.Api.Resilience.HydrationMaxConcurrency).IsEqualTo(16);
    }

    [Test]
    public async Task BatchBytesThresholdAcceptsBlankAsNull()
    {
        // Blank on row 28 must clear to null (PromptNullableInt contract), not fall back to the existing value.
        var console = NewConsole();
        EditField(console, fieldIndex: 29, string.Empty);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Api.BatchBytesThreshold).IsNull();
    }

    [Test]
    public async Task DbRetryAttemptsRePromptsOnOutOfRangeInt()
    {
        // 9999 breaches the [1..100] bound on row 29; second answer sticks. Pins that the
        // tuning fields share PromptInt's validator with the port rows.
        var console = NewConsole();
        EditField(console, fieldIndex: 30, "9999", "5");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Api.Resilience.DbRetryAttempts).IsEqualTo(5);
        await Assert.That(console.Output).Contains("must be an integer in");
    }

    [Test]
    public async Task FormShowsEditedValueInMenuRow()
    {
        // Post-edit re-render must echo the freshly-typed value on the row.
        var console = NewConsole();
        EditField(console, fieldIndex: 15, "5005");
        ConfirmForm(console);

        PromptWithoutDetection(console);

        await Assert.That(console.Output).Contains("5005");
    }

    [Test]
    public async Task FormMasksOAuthSecretsInMenuRow()
    {
        // Runs unmasked (maskSecrets:false) to prove the Mask() Show() callback is what
        // hides the value in the row — Spectre's per-field Secret() is covered separately by
        // MaskSecretsHidesOAuthEchoInPromptOutput. The raw secret WILL appear in the
        // TextPrompt echo; the guard is that it never appears NEXT TO the label.
        const string secret = "google-secret-xyz";
        var console = NewConsole();
        EditField(console, fieldIndex: 35, secret);
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        // <set> next to the edited row, <empty> next to the two unedited ones.
        await Assert.That(output).Contains("<set>");
        await Assert.That(output).Contains("<empty>");

        // The actual masking guard.
        var leakedMenuRow = new Regex(@"Google OAuth client secret\s+google-secret-xyz");
        await Assert.That(leakedMenuRow.IsMatch(output)).IsFalse();

        // Sanity: without this, the guard above passes vacuously.
        await Assert.That(output).Contains(secret);
    }

    [Test]
    public async Task EditingUpdateSectionCapturesValues()
    {
        // Happy-path edit of every row in the update section (44..52).
        var console = NewConsole();
        EditField(console, fieldIndex: 44, "y");                                    // Chain updates
        EditField(console, fieldIndex: 45, "n");                                    // Bootstrapper self-update before images
        Navigate(console, 46);
        console.Input.PushKey(ConsoleKey.Enter);                                  // Channel (SelectionPrompt; default stable)
        EditField(console, fieldIndex: 47, "n");                                    // Self-update on bootstrap
        EditField(console, fieldIndex: 48, "n");                                    // Rollback bootstrapper on failure
        EditField(console, fieldIndex: 49, "300");                                  // HealthCheckTimeoutSeconds
        EditField(console, fieldIndex: 50, "y");                                    // AutoRestoreOnFailure := true
        EditField(console, fieldIndex: 51, "n");                                    // RecreateOnUpdate := false
        EditField(console, fieldIndex: 52, "interfold-api,octocon-web");            // Services
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.Update.Enabled).IsTrue();
        await Assert.That(config.Deployment.Update.HealthCheckTimeoutSeconds).IsEqualTo(300);
        await Assert.That(config.Deployment.Update.AutoRestoreOnFailure).IsTrue();
        await Assert.That(config.Deployment.Update.RecreateOnUpdate).IsFalse();
        await Assert.That(config.Deployment.Update.Services.Length).IsEqualTo(2);
        await Assert.That(config.Deployment.Update.Services).Contains("interfold-api");
        await Assert.That(config.Deployment.Update.Services).Contains("octocon-web");
    }

    [Test]
    public async Task UpdateHealthCheckTimeoutRePromptsOnOutOfRange()
    {
        // 9999 breaches the [1..3600] bound; second answer sticks.
        var console = NewConsole();
        EditField(console, fieldIndex: 49, "9999", "60");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.Update.HealthCheckTimeoutSeconds).IsEqualTo(60);
        await Assert.That(console.Output).Contains("must be an integer in");
    }

    [Test]
    public async Task UpdateServicesPromptRejectsUnknownEntry()
    {
        // Unknown entry re-prompts against ValidUpdateServices; second answer sticks.
        var console = NewConsole();
        EditField(console, fieldIndex: 52,
            "msg-db,not-a-real-service",
            "msg-db,scylla");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.Update.Services.Length).IsEqualTo(2);
        await Assert.That(config.Deployment.Update.Services).Contains("msg-db");
        await Assert.That(config.Deployment.Update.Services).Contains("scylla");
        await Assert.That(console.Output).Contains("not a known compose service");
    }

    [Test]
    public async Task UpdateServicesBlankClearsWhitelist()
    {
        // Blank = "every service" (stored as empty array) — the un-scope path in the UI.
        var console = NewConsole();
        EditField(console, fieldIndex: 52, string.Empty);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.Update.Services.Length).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateSectionEmptyDefaults()
    {
        // Locks the "manual only" property-initialiser defaults so a flipped default is caught.
        var console = NewConsole();
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.Update.Enabled).IsFalse();
        await Assert.That(config.Deployment.Update.HealthCheckTimeoutSeconds).IsEqualTo(180);
        await Assert.That(config.Deployment.Update.AutoRestoreOnFailure).IsFalse();
        await Assert.That(config.Deployment.Update.RecreateOnUpdate).IsTrue();
        await Assert.That(config.Deployment.Update.Services.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ExistingSeed_ConfirmImmediately_PreservesCustomValues()
    {
        var existing = TestSupport.MakeConfig(tweak: c =>
        {
            c.Edge.Hosts = ["custom.example.com", "10.0.0.5"];
            c.Edge.Certificates.RootCaName = "Custom Root CA";
            c.Edge.Certificates.CertYears = 10;
            c.Edge.Ports.Https = 5443;
            c.Api.Firebase.AndroidConfigPath = "/opt/firebase/google-services.json";
            c.Api.Firebase.WebConfigPath = "/opt/firebase/firebase-web-config.json";
        });

        var console = NewConsole();
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => null,
            hostnameProbe: () => null,
            existing: existing);

        await Assert.That(config.Edge.Hosts).IsEquivalentTo(["custom.example.com", "10.0.0.5"])
            .Because("--reconfigure must open the form pre-filled; confirm-without-edits must keep Hosts.");
        await Assert.That(config.Edge.Certificates.RootCaName).IsEqualTo("Custom Root CA");
        await Assert.That(config.Edge.Certificates.CertYears).IsEqualTo(10);
        await Assert.That(config.Edge.Ports.Https).IsEqualTo(5443);
        await Assert.That(config.Api.Firebase.AndroidConfigPath).IsEqualTo("/opt/firebase/google-services.json");
        await Assert.That(config.Api.Firebase.WebConfigPath).IsEqualTo("/opt/firebase/firebase-web-config.json");
    }

    [Test]
    public async Task MaskSecretsHidesOAuthEchoInPromptOutput()
    {
        // Prod path (maskSecrets:true) → Secret('*') masks the echo. PushTextWithEnter's
        // per-char + Enter sequence matches what ReadKey consumes.
        const string secret = "google-secret-xyz";
        var console = NewConsole();
        EditField(console, fieldIndex: 35, secret);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console, maskSecrets: true);

        // Value still lands on the config; masking is display-only.
        await Assert.That(config.Api.OAuth.GoogleClientSecret).IsEqualTo(secret);
        await Assert.That(console.Output).DoesNotContain(secret);
    }

    /// <summary>
    /// Minimal service-account fixture so the scanner's <c>"type": "service_account"</c>
    /// sniff finds a valid candidate. FirebasePhase-level parsing is validated separately.
    /// </summary>
    private const string FirebaseServiceAccountFixture = /*lang=json,strict*/ """
    {
      "type": "service_account",
      "project_id": "octocon-test",
      "private_key_id": "abc",
      "private_key": "-----BEGIN PRIVATE KEY-----\nfake\n-----END PRIVATE KEY-----\n",
      "client_email": "sa@octocon.iam.gserviceaccount.com",
      "client_id": "1",
      "auth_uri": "https://accounts.google.com/o/oauth2/auth",
      "token_uri": "https://oauth2.googleapis.com/token"
    }
    """;



    [Test]
    public async Task FirebaseAutoDetectBranchPopulatesAllFourPathsFromFolder()
    {
        // End-to-end drive of the scanner → wizard → config wiring on the auto-detect branch.
        using var scratch = TestSupport.NewScratchDir("firebase-wizard");
        var folder = scratch.Path;
        var android = Path.Combine(folder, "google-services.json");
        var ios = Path.Combine(folder, "GoogleService-Info.plist");
        var web = Path.Combine(folder, "firebase-web-config.json");
        var sa = Path.Combine(folder, "octocon-firebase-adminsdk-abc.json");
        File.WriteAllText(android, "{}");
        File.WriteAllText(ios, "<plist/>");
        File.WriteAllText(web, "{}");
        File.WriteAllText(sa, FirebaseServiceAccountFixture);

        var console = NewConsole();
        Navigate(console, downArrows: 53);
        // Auto-detect is the first choice — Enter without a DownArrow.
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter(folder);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Api.Firebase.AndroidConfigPath).IsEqualTo(Path.GetFullPath(android));
        await Assert.That(config.Api.Firebase.IosConfigPath).IsEqualTo(Path.GetFullPath(ios));
        await Assert.That(config.Api.Firebase.WebConfigPath).IsEqualTo(Path.GetFullPath(web));
        await Assert.That(config.Api.Firebase.ServiceAccountPath).IsEqualTo(Path.GetFullPath(sa));
    }

    [Test]
    public async Task FirebasePerFileBranchWritesFourExplicitPaths()
    {
        // Per-file branch: four sequential TextPrompts, each guarded by File.Exists.
        using var scratch = TestSupport.NewScratchDir("firebase-wizard");
        var folder = scratch.Path;
        var android = Path.Combine(folder, "google-services.json");
        var ios = Path.Combine(folder, "GoogleService-Info.plist");
        var web = Path.Combine(folder, "firebase-web-config.json");
        var sa = Path.Combine(folder, "service-account.json");
        File.WriteAllText(android, "{}");
        File.WriteAllText(ios, "<plist/>");
        File.WriteAllText(web, "{}");
        File.WriteAllText(sa, FirebaseServiceAccountFixture);

        var console = NewConsole();
        Navigate(console, downArrows: 53);
        // Per-file is the 2nd choice — one DownArrow before Enter.
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter(android);
        console.Input.PushTextWithEnter(ios);
        console.Input.PushTextWithEnter(web);
        console.Input.PushTextWithEnter(sa);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Api.Firebase.AndroidConfigPath).IsEqualTo(android);
        await Assert.That(config.Api.Firebase.IosConfigPath).IsEqualTo(ios);
        await Assert.That(config.Api.Firebase.WebConfigPath).IsEqualTo(web);
        await Assert.That(config.Api.Firebase.ServiceAccountPath).IsEqualTo(sa);
    }

    [Test]
    public async Task FirebaseCancelBranchLeavesSectionAtDefaults()
    {
        // Cancel is the 4th choice — three DownArrows. Every *Path must stay empty.
        var console = NewConsole();
        Navigate(console, downArrows: 53);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Api.Firebase.AndroidConfigPath).IsEmpty();
        await Assert.That(config.Api.Firebase.IosConfigPath).IsEmpty();
        await Assert.That(config.Api.Firebase.WebConfigPath).IsEmpty();
        await Assert.That(config.Api.Firebase.ServiceAccountPath).IsEmpty();
    }
}
