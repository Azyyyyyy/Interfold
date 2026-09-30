using System.Net;
using Interfold.Bootstrapper.Configuration;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration.Validation;
using Interfold.Shared.Contracts.Enums;
using Spectre.Console;

namespace Interfold.Bootstrapper.Phases;

/// <summary>First-run setup. Guided setup is the default selection; Advanced opens
/// <see cref="ConfigPhase.PromptForConfig"/>. The summary can hand the guided answers
/// to that same editor, or discard them and ask for the mode again.</summary>
internal static class GuidedConfigPrompt
{
    internal const string ChoiceGuided = "Guided setup (recommended)";
    internal const string ChoiceAdvanced = "Advanced (all options)";

    internal const string ReachabilityTitle = "How will people reach this server?";
    internal const string ReachabilityLan = "This machine or my local network";
    internal const string ReachabilityCloudflare = "Public internet (Cloudflare Tunnel)";

    internal const string PersistenceTitle = "Where should data be stored?";
    internal const string PersistenceSqlite = "SQLite (recommended)";
    internal const string PersistenceScylla = "Scylla and Postgres";

    internal const string RoutingTitle = "How should the site and API be addressed?";
    internal const string RoutingPath = "One address (site and API)";
    internal const string RoutingSeparate = "Separate API and web hostnames";

    internal const string SignInTitle = "Sign-in";
    internal const string SignInNone = "None";
    internal const string SignInGoogle = "Google";
    internal const string SignInDiscord = "Discord";
    internal const string SignInBoth = "Google and Discord";

    internal const string GoogleClientIdLabel = "Google OAuth client ID";
    internal const string DiscordClientIdLabel = "Discord OAuth client ID";
    internal const string AppleClientIdLabel = "Apple OAuth client ID";
    internal const string AppleQuestion = "Add Apple sign-in";
    internal const string PortsQuestion = "Use the standard web ports (80 and 443)";

    internal const string SummaryConfirm = "Confirm and continue";
    internal const string SummaryStartOver = "Start over";
    internal const string SummaryAdvanced = "Open advanced editor";

    internal const string UpdatesUnavailableMessage =
        "Automatic updates are not available until scheduled backups are turned on. Updates run after each successful backup.";

    internal const string UpdateQuestion = "Install updates after each successful backup";
    internal const string BootstrapperUpdateQuestion = "Also update the bootstrapper before the container images";
    internal const string FirebaseQuestion = "Set up Firebase push notifications";
    internal const string OtlpQuestion = "Export telemetry to an OpenTelemetry collector";
    internal const string OtlpClientOverrideLabel = "Client OTLP/HTTP override (blank = use the server endpoint)";

    internal const string CadenceDaily = "Daily (recommended)";
    internal const string CadenceWeekly = "Weekly";
    internal const string CadenceCustom = "I'll type a schedule";

    private enum Outcome { Confirm, StartOver, Advanced }

    internal static BootstrapConfig Run(
        IAnsiConsole console,
        bool maskSecrets = false,
        Func<IPAddress?>? localAddressProbe = null,
        Func<string?>? hostnameProbe = null)
    {
        while (true)
        {
            var mode = Choose(console,
                "[bold]How do you want to configure Interfold?[/]\n" +
                "[grey]Guided setup asks a few questions. Advanced lists every option.[/]",
                ChoiceGuided,
                ChoiceAdvanced);
            if (mode == ChoiceAdvanced)
            {
                return ConfigPhase.PromptForConfig(console, maskSecrets, localAddressProbe, hostnameProbe);
            }

            var (outcome, config) = Ask(console, maskSecrets, localAddressProbe, hostnameProbe);
            switch (outcome)
            {
                case Outcome.Confirm:
                    return config;
                case Outcome.Advanced:
                    return ConfigPhase.PromptForConfig(
                        console, maskSecrets, localAddressProbe, hostnameProbe, existing: config);
            }
        }
    }

    private static (Outcome Outcome, BootstrapConfig Config) Ask(
        IAnsiConsole console,
        bool maskSecrets,
        Func<IPAddress?>? localAddressProbe,
        Func<string?>? hostnameProbe)
    {
        var reach = Choose(console, $"[bold]{ReachabilityTitle}[/]", ReachabilityLan, ReachabilityCloudflare);
        var lan = reach == ReachabilityLan;

        var store = Choose(console, $"[bold]{PersistenceTitle}[/]", PersistenceSqlite, PersistenceScylla);
        var includeWeb = AskYesNo(console, "Include the web UI (interfold-web)", fallback: false);

        var seed = SeedHosts(localAddressProbe, hostnameProbe, dnsOnly: !lan);
        var hosts = PromptHosts(
            console,
            lan
                ? "Host (domain, IP, or CIDR). Comma-separated if more than one"
                : "Public hostname (DNS name, comma-separated if more than one)",
            seed,
            dnsOnly: !lan);

        var config = new BootstrapConfig();
        config.Deployment.IncludeWeb = includeWeb;
        config.Datastores.Persistence = store == PersistenceSqlite
            ? PersistenceMode.Sqlite
            : PersistenceMode.ScyllaPostgres;
        config.Edge.Hosts = hosts;

        var anyDns = hosts.Any(IsDns);
        if (includeWeb && anyDns)
        {
            var routing = Choose(console, $"[bold]{RoutingTitle}[/]", RoutingPath, RoutingSeparate);
            if (routing == RoutingSeparate)
            {
                config.Edge.Routing.Mode = EdgeRoutingMode.Subdomain;
                config.Edge.Routing.ApiHost = PromptDnsName(console, "API hostname");
                config.Edge.Routing.WebHost = PromptDnsName(console, "Web hostname");
            }
        }

        if (lan)
        {
            config.Edge.TlsMode = EdgeTlsMode.PrivateCa;
            config.Edge.Certificates.TrustStoreInstall = AskYesNo(
                console, "Install the root CA into this machine's trust store", fallback: true);
            // Cloudflare does not publish host ports, so this question is LAN-only.
            PromptPublishedPorts(console, config);
        }
        else
        {
            // ValidateEdge coerces tlsMode to none once the tunnel is on.
            config.Edge.Cloudflare.Enabled = true;
            config.Edge.Cloudflare.ApiToken = PromptRequired(console, "Cloudflare API token", maskSecrets);
            config.Edge.Cloudflare.Access.Enabled = AskYesNo(
                console, "Restrict who can open the site with Cloudflare Access", fallback: false);
            if (config.Edge.Cloudflare.Access.Enabled)
            {
                PromptAccessAllowlist(console, config.Edge.Cloudflare.Access);
                config.Api.OAuth.GoogleClientId = PromptRequired(console, GoogleClientIdLabel, secret: false);
                config.Api.OAuth.GoogleClientSecret = PromptRequired(console, "Google OAuth client secret", maskSecrets);
            }
        }

        if (config.Edge.Cloudflare.Access.Enabled)
        {
            if (AskYesNo(console, "Add Discord sign-in", fallback: false))
            {
                config.Api.OAuth.DiscordClientId = PromptRequired(console, DiscordClientIdLabel, secret: false);
                config.Api.OAuth.DiscordClientSecret = PromptRequired(console, "Discord OAuth client secret", maskSecrets);
            }

            PromptApple(console, config, maskSecrets);
        }
        else
        {
            PromptSignIn(console, config, maskSecrets);
        }

        PromptOptionalFeatures(console, config);

        var outcome = ShowSummary(console, config, ipOnly: !anyDns);
        return (outcome, config);
    }

    /// <summary>Hostname before IP, matching <see cref="ConfigPhase.PromptForConfig"/>,
    /// so the primary host is the mDNS name when both resolve. Cloudflare cannot publish
    /// an IP, so that branch drops non-DNS seeds rather than offering a default that
    /// would fail validation.</summary>
    private static List<string> SeedHosts(
        Func<IPAddress?>? localAddressProbe,
        Func<string?>? hostnameProbe,
        bool dnsOnly)
    {
        var seed = new List<string>();
        var hostname = (hostnameProbe ?? (() => null))();
        if (hostname is not null)
        {
            seed.Add(hostname);
        }

        if (!dnsOnly)
        {
            var detected = (localAddressProbe ?? LocalAddressDetector.TryDetectPrimaryIp)();
            if (detected is not null)
            {
                seed.Add(detected.ToString());
            }
        }

        return seed;
    }

    private static List<string> PromptHosts(
        IAnsiConsole console,
        string label,
        IReadOnlyList<string> fallback,
        bool dnsOnly)
    {
        var fallbackText = string.Join(",", fallback);
        var prompt = new TextPrompt<string>($"{label}:")
            .AllowEmpty()
            .Validate(raw => ValidateHostList(raw, fallback, dnsOnly));
        if (fallback.Count > 0)
        {
            prompt.DefaultValue(fallbackText);
        }

        var answered = console.Prompt(prompt);
        if (string.IsNullOrWhiteSpace(answered))
        {
            return [.. fallback];
        }

        return answered.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static ValidationResult ValidateHostList(string? raw, IReadOnlyList<string> fallback, bool dnsOnly)
    {
        var trimmed = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            return fallback.Count == 0
                ? ValidationResult.Error("[red]at least one host is required[/]")
                : ValidationResult.Success();
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return ValidationResult.Error("[red]at least one host is required[/]");
        }

        var parsed = new List<HostEntry>(parts.Length);
        foreach (var part in parts)
        {
            try
            {
                parsed.Add(HostParser.Parse(part));
            }
            catch (FormatException ex)
            {
                return ValidationResult.Error($"[red]{ex.Message}[/]");
            }
        }

        if (dnsOnly && parsed.Any(entry => entry.Kind != HostKind.Dns))
        {
            return ValidationResult.Error(
                "[red]Cloudflare Tunnel needs a DNS hostname, not an IP address[/]");
        }

        if (!parsed.Any(entry => entry.IsLeafEligible))
        {
            return ValidationResult.Error(
                "[red]at least one DNS name or IP is required (a CIDR alone cannot be the public host)[/]");
        }

        return ValidationResult.Success();
    }

    private static string PromptDnsName(IAnsiConsole console, string label)
    {
        return console.Prompt(new TextPrompt<string>($"{label}:")
            .Validate(raw =>
            {
                var trimmed = raw?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(trimmed) || trimmed.Contains(','))
                {
                    return ValidationResult.Error("[red]enter a single DNS hostname[/]");
                }

                try
                {
                    var entry = HostParser.Parse(trimmed);
                    return entry.Kind == HostKind.Dns
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]must be a DNS hostname[/]");
                }
                catch (FormatException ex)
                {
                    return ValidationResult.Error($"[red]{ex.Message}[/]");
                }
            })).Trim();
    }

    private static void PromptAccessAllowlist(IAnsiConsole console, EdgeCloudflareAccessSection access)
    {
        while (true)
        {
            access.AllowedEmails = PromptCsv(console, "Access allowed emails (comma-separated, blank to skip)");
            access.AllowedEmailDomains = PromptCsv(console, "Access allowed email domains (comma-separated, blank to skip)");
            if (access.AllowedEmails.Count > 0 || access.AllowedEmailDomains.Count > 0)
            {
                return;
            }

            console.MarkupLine("[red]Enter at least one email address or email domain.[/]");
        }
    }

    private static void PromptSignIn(IAnsiConsole console, BootstrapConfig config, bool maskSecrets)
    {
        var choice = Choose(console, $"[bold]{SignInTitle}[/]", SignInNone, SignInGoogle, SignInDiscord, SignInBoth);
        if (choice is SignInGoogle or SignInBoth)
        {
            config.Api.OAuth.GoogleClientId = PromptRequired(console, GoogleClientIdLabel, secret: false);
            config.Api.OAuth.GoogleClientSecret = PromptRequired(console, "Google OAuth client secret", maskSecrets);
        }

        if (choice is SignInDiscord or SignInBoth)
        {
            config.Api.OAuth.DiscordClientId = PromptRequired(console, DiscordClientIdLabel, secret: false);
            config.Api.OAuth.DiscordClientSecret = PromptRequired(console, "Discord OAuth client secret", maskSecrets);
        }

        PromptApple(console, config, maskSecrets);
    }

    private static void PromptApple(IAnsiConsole console, BootstrapConfig config, bool maskSecrets)
    {
        if (!AskYesNo(console, AppleQuestion, fallback: false))
        {
            return;
        }

        config.Api.OAuth.AppleClientId = PromptRequired(console, AppleClientIdLabel, secret: false);
        config.Api.OAuth.AppleClientSecret = PromptRequired(console, "Apple OAuth client secret", maskSecrets);
    }

    private static void PromptPublishedPorts(IAnsiConsole console, BootstrapConfig config)
    {
        if (AskYesNo(console, PortsQuestion, fallback: true))
        {
            return;
        }

        config.Edge.Ports.Http = PromptPort(console, "HTTP port", config.Edge.Ports.Http, forbidden: null);
        config.Edge.Ports.Https = PromptPort(console, "HTTPS port", config.Edge.Ports.Https, forbidden: config.Edge.Ports.Http);
    }

    private static int PromptPort(IAnsiConsole console, string label, int fallback, int? forbidden)
    {
        return console.Prompt(
            new TextPrompt<int>($"{label}:")
                .DefaultValue(fallback)
                .ValidationErrorMessage("[red]must be an integer in [[1..65535]][/]")
                .Validate(port =>
                {
                    if (port is < 1 or > 65535)
                    {
                        return ValidationResult.Error("[red]must be an integer in [[1..65535]][/]");
                    }

                    return port == forbidden
                        ? ValidationResult.Error("[red]HTTP and HTTPS must use different ports[/]")
                        : ValidationResult.Success();
                }));
    }

    private static void PromptOptionalFeatures(IAnsiConsole console, BootstrapConfig config)
    {
        if (!AskYesNo(console, "Enable scheduled backups", fallback: false))
        {
            console.MarkupLine($"[yellow]{UpdatesUnavailableMessage}[/]");
        }
        else
        {
            config.Deployment.Backup.Enabled = true;
            config.Deployment.Backup.Schedule = PromptBackupSchedule(console);
            config.Deployment.Backup.RetainCount = console.Prompt(
                new TextPrompt<int>("How many backup archives should be kept per database?")
                    .DefaultValue(config.Deployment.Backup.RetainCount)
                    .ValidationErrorMessage("[red]must be an integer in [[1..1000]][/]")
                    .Validate(n => n is >= 1 and <= 1000));

            console.MarkupLine(
                "[grey]Yes pulls new images and recreates containers. If the health check fails, the backup taken just before the update is restored. The release channel stays stable.[/]");
            if (AskYesNo(console, UpdateQuestion, fallback: false))
            {
                ApplyImageUpdatePreset(config);
                console.MarkupLine(
                    "[grey]Yes runs update-self before the image update. If the health check fails, the previous bootstrapper binary is restored.[/]");
                if (AskYesNo(console, BootstrapperUpdateQuestion, fallback: false))
                {
                    config.Deployment.Update.Bootstrapper.Enabled = true;
                    config.Deployment.Update.Bootstrapper.AutoRollbackOnFailure = true;
                }
            }
        }

        if (AskYesNo(console, FirebaseQuestion, fallback: false))
        {
            ConfigPhase.PromptGuidedFirebase(console, config.Api.Firebase);
        }

        if (!AskYesNo(console, OtlpQuestion, fallback: false))
        {
            return;
        }

        config.Observability.OtlpEndpoint = PromptHttpUrl(console, "OTLP collector URL", required: true);
        config.Observability.AdvertiseOtlpToClients = AskYesNo(
            console, "Advertise that endpoint to clients", fallback: false);
        if (config.Observability.AdvertiseOtlpToClients)
        {
            config.Observability.ClientOtlpHttpEndpoint = PromptHttpUrl(console, OtlpClientOverrideLabel, required: false);
        }
    }

    private static void ApplyImageUpdatePreset(BootstrapConfig config)
    {
        var update = config.Deployment.Update;
        update.Enabled = true;
        update.RecreateOnUpdate = true;
        update.AutoRestoreOnFailure = true;
        update.HealthCheckTimeoutSeconds = 180;
        update.Services = [];
        update.Bootstrapper.Channel = BootstrapperReleaseChannel.Stable;
    }

    private static string PromptBackupSchedule(IAnsiConsole console)
    {
        var cadence = Choose(console, "[bold]How often should backups run?[/]", CadenceDaily, CadenceWeekly, CadenceCustom);
        if (cadence == CadenceWeekly)
        {
            return "weekly";
        }

        if (cadence == CadenceDaily)
        {
            return "daily";
        }

        return console.Prompt(new TextPrompt<string>("Backup schedule (systemd OnCalendar, e.g. daily, weekly, Mon..Fri 03:30):")
            .Validate(raw => ConfigPhase.IsAllowedBackupSchedule(raw)
                ? ValidationResult.Success()
                : ValidationResult.Error(
                    "[red]use letters, digits, spaces, and . - : , * / (for example daily, weekly, or Mon..Fri 03:30)[/]")));
    }

    private static string PromptHttpUrl(IAnsiConsole console, string label, bool required)
    {
        var prompt = new TextPrompt<string>($"{label}:")
            .AllowEmpty()
            .Validate(raw =>
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return required
                        ? ValidationResult.Error("[red]an absolute http(s) URL is required[/]")
                        : ValidationResult.Success();
                }

                return AbsoluteHttpUriAttribute.IsAbsoluteHttpUri(raw.Trim())
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]must be an absolute http(s) URL[/]");
            });
        var answered = console.Prompt(prompt);
        return string.IsNullOrWhiteSpace(answered) ? string.Empty : answered.Trim();
    }

    private static string DescribeBackups(BootstrapConfig config)
    {
        if (!config.Deployment.Backup.Enabled)
        {
            return "off";
        }

        return $"{config.Deployment.Backup.Schedule}, keep {config.Deployment.Backup.RetainCount}";
    }

    private static string DescribeUpdates(BootstrapConfig config)
    {
        if (!config.Deployment.Backup.Enabled)
        {
            return "not available (scheduled backups are off)";
        }

        if (!config.Deployment.Update.Enabled)
        {
            return "off";
        }

        var bootstrapper = config.Deployment.Update.Bootstrapper.Enabled
            ? "on, bootstrapper updated, rollback on failure"
            : "on, bootstrapper not updated";
        return $"{bootstrapper} (stable images, restore the pre-update backup if the health check fails)";
    }

    private static string DescribeOtlp(BootstrapConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Observability.OtlpEndpoint))
        {
            return "off";
        }

        var advertised = config.Observability.AdvertiseOtlpToClients
            ? "advertised to clients"
            : "not advertised";
        if (string.IsNullOrWhiteSpace(config.Observability.ClientOtlpHttpEndpoint))
        {
            return $"{config.Observability.OtlpEndpoint}, {advertised}";
        }

        return $"{config.Observability.OtlpEndpoint}, {advertised}, client override {config.Observability.ClientOtlpHttpEndpoint}";
    }

    private static Outcome ShowSummary(IAnsiConsole console, BootstrapConfig config, bool ipOnly)
    {
        ConfigPhase.ResolveDerivedDefaults(config);

        var lan = !config.Edge.Cloudflare.Enabled;
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Your answers[/]");
        table.AddColumn("Setting");
        table.AddColumn("Value");

        void Row(string setting, string value) => table.AddRow(setting, Markup.Escape(value));

        Row("Reachability", lan
            ? "This machine or local network (private CA)"
            : "Public internet (Cloudflare Tunnel)");
        Row("Host", string.Join(", ", config.Edge.Hosts));
        Row("Web UI", config.Deployment.IncludeWeb ? "yes" : "no");
        Row("Routing", DescribeRouting(config, ipOnly));
        Row("Persistence", DescribePersistence(config));
        if (lan)
        {
            Row("TLS", config.Edge.Certificates.TrustStoreInstall
                ? "private CA, trust store yes"
                : "private CA, trust store no");
        }
        else
        {
            Row("Tunnel", DescribeTunnel(config));
        }

        Row("Ports", DescribePorts(config));

        Row("Sign-in", DescribeSignIn(config));
        Row("Backups", DescribeBackups(config));
        Row("Updates", DescribeUpdates(config));
        Row("Firebase", ConfigPhase.ShowFirebaseState(config.Api.Firebase));
        Row("OTLP", DescribeOtlp(config));
        Row("Public URL", config.Api.OAuth.CallbackBaseUrl);

        console.Write(table);
        var choice = Choose(console,
            "[bold]Continue with these answers?[/]",
            SummaryConfirm,
            SummaryStartOver,
            SummaryAdvanced);
        return choice switch
        {
            SummaryStartOver => Outcome.StartOver,
            SummaryAdvanced => Outcome.Advanced,
            _ => Outcome.Confirm,
        };
    }

    private static string DescribeRouting(BootstrapConfig config, bool ipOnly)
    {
        if (config.Edge.Routing.Mode == EdgeRoutingMode.Subdomain)
        {
            return $"separate hostnames ({config.Edge.Routing.ApiHost} / {config.Edge.Routing.WebHost})";
        }

        return ipOnly
            ? "path (IP hosts stay on one address)"
            : "path (one address)";
    }

    private static string DescribePersistence(BootstrapConfig config)
    {
        if (config.UsesSqlite)
        {
            return "SQLite";
        }

        return "Scylla and Postgres (" +
               $"{config.Datastores.Cql.Backend.ToWire()}, " +
               $"database {config.Datastores.Postgres.Database}, " +
               $"keyspace {config.Datastores.Cql.Keyspace.ToWire()})";
    }

    private static string DescribePorts(BootstrapConfig config)
    {
        if (config.Edge.Cloudflare.Enabled)
        {
            return "not published (Cloudflare Tunnel)";
        }

        return $"{config.Edge.Ports.Http} and {config.Edge.Ports.Https}";
    }

    private static string DescribeTunnel(BootstrapConfig config)
    {
        var access = config.Edge.Cloudflare.Access;
        var tunnel = $"Cloudflare Tunnel \"{config.Edge.Cloudflare.TunnelName}\", token set";
        if (!access.Enabled)
        {
            return $"{tunnel}, Access off";
        }

        var allow = new List<string>();
        if (access.AllowedEmails.Count > 0)
        {
            allow.Add(string.Join(", ", access.AllowedEmails));
        }

        if (access.AllowedEmailDomains.Count > 0)
        {
            allow.Add(string.Join(", ", access.AllowedEmailDomains));
        }

        return $"{tunnel}, Access on ({string.Join("; ", allow)})";
    }

    private static string DescribeSignIn(BootstrapConfig config)
    {
        var providers = new List<string>(3);
        providers.Add(string.IsNullOrEmpty(config.Api.OAuth.GoogleClientId) ? "Google (empty)" : "Google (set)");
        providers.Add(string.IsNullOrEmpty(config.Api.OAuth.DiscordClientId) ? "Discord (empty)" : "Discord (set)");
        providers.Add(string.IsNullOrEmpty(config.Api.OAuth.AppleClientId) ? "Apple (empty)" : "Apple (set)");
        if (string.IsNullOrEmpty(config.Api.OAuth.GoogleClientId)
            && string.IsNullOrEmpty(config.Api.OAuth.DiscordClientId)
            && string.IsNullOrEmpty(config.Api.OAuth.AppleClientId))
        {
            return "none";
        }

        return string.Join(", ", providers);
    }

    private static List<string> PromptCsv(IAnsiConsole console, string label)
    {
        var raw = console.Prompt(new TextPrompt<string>($"{label}:").AllowEmpty());
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string PromptRequired(IAnsiConsole console, string label, bool secret)
    {
        var prompt = new TextPrompt<string>($"{label}:")
            .Validate(raw => string.IsNullOrWhiteSpace(raw)
                ? ValidationResult.Error("[red]required[/]")
                : ValidationResult.Success());
        if (secret)
        {
            prompt.Secret('*');
        }

        return console.Prompt(prompt).Trim();
    }

    private static bool AskYesNo(IAnsiConsole console, string question, bool fallback) =>
        console.Prompt(new ConfirmationPrompt($"{question}?") { DefaultValue = fallback });

    private static string Choose(IAnsiConsole console, string title, params string[] choices) =>
        console.Prompt(new SelectionPrompt<string>().Title(title).AddChoices(choices));

    private static bool IsDns(string raw)
    {
        try
        {
            return HostParser.Parse(raw).Kind == HostKind.Dns;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
