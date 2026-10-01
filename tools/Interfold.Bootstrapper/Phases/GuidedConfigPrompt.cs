using System.Net;
using System.Text.Json;
using Interfold.Bootstrapper.Configuration;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration.Validation;
using Interfold.Shared.Contracts.Enums;
using Spectre.Console;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Guided or advanced setup. Guided setup is the default selection; Advanced opens
/// <see cref="ConfigPhase.PromptForConfig"/>. On <c>--reconfigure</c> both paths start from
/// the existing file, and guided Enter keeps the current answer. The summary can hand
/// the result to that editor, or discard it and ask for the mode again.</summary>
internal static class GuidedConfigPrompt
{
    internal const string ChoiceGuided = "Guided setup (recommended)";
    internal const string ChoiceAdvanced = "Advanced (all options)";

    internal const string ReachabilityTitle = "How will people reach this server?";
    internal const string ReachabilityLan = "This machine or my local network";
    internal const string ReachabilityCloudflare = "Public internet (Cloudflare Tunnel)";

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
        Func<string?>? hostnameProbe = null,
        BootstrapConfig? existing = null)
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
                return ConfigPhase.PromptForConfig(
                    console, maskSecrets, localAddressProbe, hostnameProbe, existing: existing);
            }

            var (outcome, config) = Ask(console, maskSecrets, localAddressProbe, hostnameProbe, existing);
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
        Func<string?>? hostnameProbe,
        BootstrapConfig? existing)
    {
        // Clone so Start over re-asks from the file, and so accepting defaults keeps
        // fields this tree never asks about.
        var config = existing is null ? new BootstrapConfig() : CloneConfig(existing);
        var derived = DerivedInputs.Capture(config);

        var reach = ChooseKeeping(
            console,
            $"[bold]{ReachabilityTitle}[/]",
            config.Edge.Cloudflare.Enabled ? ReachabilityCloudflare : ReachabilityLan,
            ReachabilityLan,
            ReachabilityCloudflare);
        var lan = reach == ReachabilityLan;

        // Scylla and Postgres stay in the advanced editor. Guided always uses SQLite.
        config.Datastores.Persistence = PersistenceMode.Sqlite;
        var includeWeb = AskYesNo(console, "Include the web UI (interfold-web)", config.Deployment.IncludeWeb);

        var seed = SeedForPrompt(config.Edge.Hosts, localAddressProbe, hostnameProbe, dnsOnly: !lan);
        var hosts = PromptHosts(
            console,
            lan
                ? "Host (domain, IP, or CIDR). Comma-separated if more than one"
                : "Public hostname (DNS name, comma-separated if more than one)",
            seed,
            dnsOnly: !lan);

        config.Deployment.IncludeWeb = includeWeb;
        config.Edge.Hosts = hosts;

        var anyDns = hosts.Any(IsDns);
        if (includeWeb && anyDns)
        {
            var routingCurrent = config.Edge.Routing.Mode == EdgeRoutingMode.Subdomain
                ? RoutingSeparate
                : RoutingPath;
            var routing = ChooseKeeping(
                console, $"[bold]{RoutingTitle}[/]", routingCurrent, RoutingPath, RoutingSeparate);
            if (routing == RoutingSeparate)
            {
                config.Edge.Routing.Mode = EdgeRoutingMode.Subdomain;
                config.Edge.Routing.ApiHost = PromptDnsName(console, "API hostname", config.Edge.Routing.ApiHost);
                config.Edge.Routing.WebHost = PromptDnsName(console, "Web hostname", config.Edge.Routing.WebHost);
            }
            else
            {
                config.Edge.Routing.Mode = EdgeRoutingMode.Path;
            }
        }

        if (lan)
        {
            config.Edge.TlsMode = EdgeTlsMode.PrivateCa;
            config.Edge.Cloudflare.Enabled = false;
            config.Edge.Certificates.TrustStoreInstall = AskYesNo(
                console,
                "Install the root CA into this machine's trust store",
                config.Edge.Certificates.TrustStoreInstall);
            // Cloudflare does not publish host ports, so this question is LAN-only.
            PromptPublishedPorts(console, config);
        }
        else
        {
            // ValidateEdge coerces tlsMode to none once the tunnel is on.
            config.Edge.Cloudflare.Enabled = true;
            config.Edge.Cloudflare.ApiToken = PromptKeep(
                console, "Cloudflare API token", config.Edge.Cloudflare.ApiToken, maskSecrets);
            var access = config.Edge.Cloudflare.Access;
            access.Enabled = AskYesNo(
                console, "Restrict who can open the site with Cloudflare Access", access.Enabled);
            if (access.Enabled)
            {
                PromptAccessAllowlist(console, access);
                config.Api.OAuth.GoogleClientId = PromptKeep(
                    console, GoogleClientIdLabel, config.Api.OAuth.GoogleClientId, secret: false);
                config.Api.OAuth.GoogleClientSecret = PromptKeep(
                    console, "Google OAuth client secret", config.Api.OAuth.GoogleClientSecret, maskSecrets);
            }
        }

        if (config.Edge.Cloudflare.Access.Enabled)
        {
            var discordOn = !string.IsNullOrEmpty(config.Api.OAuth.DiscordClientId);
            if (AskYesNo(console, "Add Discord sign-in", discordOn))
            {
                config.Api.OAuth.DiscordClientId = PromptKeep(
                    console, DiscordClientIdLabel, config.Api.OAuth.DiscordClientId, secret: false);
                config.Api.OAuth.DiscordClientSecret = PromptKeep(
                    console, "Discord OAuth client secret", config.Api.OAuth.DiscordClientSecret, maskSecrets);
            }
            else
            {
                config.Api.OAuth.DiscordClientId = string.Empty;
                config.Api.OAuth.DiscordClientSecret = string.Empty;
            }

            PromptApple(console, config, maskSecrets);
        }
        else
        {
            PromptSignIn(console, config, maskSecrets);
        }

        PromptOptionalFeatures(console, config);
        ClearDerivedIfChanged(derived, config);

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

    /// <summary>An existing host list wins over mDNS/IP detection so reconfigure keeps the
    /// published name. Cloudflare still drops non-DNS entries.</summary>
    private static List<string> SeedForPrompt(
        IReadOnlyList<string> configured,
        Func<IPAddress?>? localAddressProbe,
        Func<string?>? hostnameProbe,
        bool dnsOnly)
    {
        if (configured.Count > 0)
        {
            var kept = dnsOnly ? configured.Where(IsDns).ToList() : configured.ToList();
            if (kept.Count > 0)
            {
                return kept;
            }
        }

        return SeedHosts(localAddressProbe, hostnameProbe, dnsOnly);
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

    private static string PromptDnsName(IAnsiConsole console, string label, string current)
    {
        var prompt = new TextPrompt<string>($"{label}:")
            .AllowEmpty()
            .Validate(raw =>
            {
                var trimmed = string.IsNullOrWhiteSpace(raw) ? current.Trim() : raw.Trim();
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
            });
        if (!string.IsNullOrWhiteSpace(current))
        {
            prompt.DefaultValue(current);
        }

        var answered = console.Prompt(prompt);
        return string.IsNullOrWhiteSpace(answered) ? current.Trim() : answered.Trim();
    }

    private static void PromptAccessAllowlist(IAnsiConsole console, EdgeCloudflareAccessSection access)
    {
        while (true)
        {
            var emails = PromptCsv(
                console, "Access allowed emails (comma-separated, blank to skip)", access.AllowedEmails);
            var domains = PromptCsv(
                console, "Access allowed email domains (comma-separated, blank to skip)", access.AllowedEmailDomains);
            if (emails.Count > 0 || domains.Count > 0)
            {
                access.AllowedEmails = emails;
                access.AllowedEmailDomains = domains;
                return;
            }

            console.MarkupLine("[red]Enter at least one email address or email domain.[/]");
        }
    }

    private static void PromptSignIn(IAnsiConsole console, BootstrapConfig config, bool maskSecrets)
    {
        var google = !string.IsNullOrEmpty(config.Api.OAuth.GoogleClientId);
        var discord = !string.IsNullOrEmpty(config.Api.OAuth.DiscordClientId);
        var current = (google, discord) switch
        {
            (true, true) => SignInBoth,
            (true, false) => SignInGoogle,
            (false, true) => SignInDiscord,
            _ => SignInNone,
        };
        var choice = ChooseKeeping(
            console, $"[bold]{SignInTitle}[/]", current, SignInNone, SignInGoogle, SignInDiscord, SignInBoth);
        if (choice is SignInGoogle or SignInBoth)
        {
            config.Api.OAuth.GoogleClientId = PromptKeep(console, GoogleClientIdLabel, config.Api.OAuth.GoogleClientId, secret: false);
            config.Api.OAuth.GoogleClientSecret = PromptKeep(console, "Google OAuth client secret", config.Api.OAuth.GoogleClientSecret, maskSecrets);
        }
        else
        {
            config.Api.OAuth.GoogleClientId = string.Empty;
            config.Api.OAuth.GoogleClientSecret = string.Empty;
        }

        if (choice is SignInDiscord or SignInBoth)
        {
            config.Api.OAuth.DiscordClientId = PromptKeep(console, DiscordClientIdLabel, config.Api.OAuth.DiscordClientId, secret: false);
            config.Api.OAuth.DiscordClientSecret = PromptKeep(console, "Discord OAuth client secret", config.Api.OAuth.DiscordClientSecret, maskSecrets);
        }
        else
        {
            config.Api.OAuth.DiscordClientId = string.Empty;
            config.Api.OAuth.DiscordClientSecret = string.Empty;
        }

        PromptApple(console, config, maskSecrets);
    }

    private static void PromptApple(IAnsiConsole console, BootstrapConfig config, bool maskSecrets)
    {
        var hasApple = !string.IsNullOrEmpty(config.Api.OAuth.AppleClientId);
        if (!AskYesNo(console, AppleQuestion, hasApple))
        {
            config.Api.OAuth.AppleClientId = string.Empty;
            config.Api.OAuth.AppleClientSecret = string.Empty;
            return;
        }

        config.Api.OAuth.AppleClientId = PromptKeep(console, AppleClientIdLabel, config.Api.OAuth.AppleClientId, secret: false);
        config.Api.OAuth.AppleClientSecret = PromptKeep(console, "Apple OAuth client secret", config.Api.OAuth.AppleClientSecret, maskSecrets);
    }

    private static void PromptPublishedPorts(IAnsiConsole console, BootstrapConfig config)
    {
        var standard = config.Edge.Ports.Http == 80 && config.Edge.Ports.Https == 443;
        if (AskYesNo(console, PortsQuestion, standard))
        {
            config.Edge.Ports.Http = 80;
            config.Edge.Ports.Https = 443;
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
        var backupsWereOn = config.Deployment.Backup.Enabled;
        if (!AskYesNo(console, "Enable scheduled backups", backupsWereOn))
        {
            config.Deployment.Backup.Enabled = false;
            console.MarkupLine($"[yellow]{UpdatesUnavailableMessage}[/]");
        }
        else
        {
            config.Deployment.Backup.Enabled = true;
            config.Deployment.Backup.Schedule = PromptBackupSchedule(console, config.Deployment.Backup.Schedule);
            config.Deployment.Backup.RetainCount = console.Prompt(
                new TextPrompt<int>("How many backup archives should be kept per database?")
                    .DefaultValue(config.Deployment.Backup.RetainCount)
                    .ValidationErrorMessage("[red]must be an integer in [[1..1000]][/]")
                    .Validate(n => n is >= 1 and <= 1000));

            var updatesWereOn = config.Deployment.Update.Enabled;
            if (!updatesWereOn)
            {
                console.MarkupLine(
                    "[grey]Yes pulls new images and recreates containers. If the health check fails, the backup taken just before the update is restored. The release channel stays stable.[/]");
            }

            if (AskYesNo(console, UpdateQuestion, updatesWereOn))
            {
                if (!updatesWereOn)
                {
                    ApplyImageUpdatePreset(config);
                }

                var bootstrapperWasOn = config.Deployment.Update.Bootstrapper.Enabled;
                if (!bootstrapperWasOn)
                {
                    console.MarkupLine(
                        "[grey]Yes runs update-self before the image update. If the health check fails, the previous bootstrapper binary is restored.[/]");
                }

                if (AskYesNo(console, BootstrapperUpdateQuestion, bootstrapperWasOn))
                {
                    if (!bootstrapperWasOn)
                    {
                        config.Deployment.Update.Bootstrapper.Enabled = true;
                        config.Deployment.Update.Bootstrapper.AutoRollbackOnFailure = true;
                    }
                }
                else
                {
                    config.Deployment.Update.Bootstrapper.Enabled = false;
                }
            }
            else
            {
                config.Deployment.Update.Enabled = false;
            }
        }

        var firebaseWasOn = FirebaseConfigured(config.Api.Firebase);
        if (AskYesNo(console, FirebaseQuestion, firebaseWasOn))
        {
            ConfigPhase.PromptGuidedFirebase(console, config.Api.Firebase);
        }
        else
        {
            config.Api.Firebase.AndroidConfigPath = string.Empty;
            config.Api.Firebase.IosConfigPath = string.Empty;
            config.Api.Firebase.WebConfigPath = string.Empty;
            config.Api.Firebase.ServiceAccountPath = string.Empty;
        }

        var otlpWasOn = !string.IsNullOrWhiteSpace(config.Observability.OtlpEndpoint);
        if (!AskYesNo(console, OtlpQuestion, otlpWasOn))
        {
            config.Observability.OtlpEndpoint = string.Empty;
            config.Observability.AdvertiseOtlpToClients = false;
            config.Observability.ClientOtlpHttpEndpoint = string.Empty;
            return;
        }

        config.Observability.OtlpEndpoint = PromptHttpUrl(
            console, "OTLP collector URL", required: true, config.Observability.OtlpEndpoint);
        config.Observability.AdvertiseOtlpToClients = AskYesNo(
            console, "Advertise that endpoint to clients", config.Observability.AdvertiseOtlpToClients);
        if (config.Observability.AdvertiseOtlpToClients)
        {
            config.Observability.ClientOtlpHttpEndpoint = PromptHttpUrl(
                console, OtlpClientOverrideLabel, required: false, config.Observability.ClientOtlpHttpEndpoint);
        }
        else
        {
            config.Observability.ClientOtlpHttpEndpoint = string.Empty;
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

    private static string PromptBackupSchedule(IAnsiConsole console, string current)
    {
        var currentChoice = current switch
        {
            "daily" => CadenceDaily,
            "weekly" => CadenceWeekly,
            _ => CadenceCustom,
        };
        var cadence = ChooseKeeping(
            console, "[bold]How often should backups run?[/]", currentChoice, CadenceDaily, CadenceWeekly, CadenceCustom);
        if (cadence == CadenceWeekly)
        {
            return "weekly";
        }

        if (cadence == CadenceDaily)
        {
            return "daily";
        }

        var prompt = new TextPrompt<string>("Backup schedule (systemd OnCalendar, e.g. daily, weekly, Mon..Fri 03:30):")
            .AllowEmpty()
            .Validate(raw =>
            {
                var candidate = string.IsNullOrWhiteSpace(raw) ? current : raw;
                return ConfigPhase.IsAllowedBackupSchedule(candidate)
                    ? ValidationResult.Success()
                    : ValidationResult.Error(
                        "[red]use letters, digits, spaces, and . - : , * / (for example daily, weekly, or Mon..Fri 03:30)[/]");
            });
        if (ConfigPhase.IsAllowedBackupSchedule(current))
        {
            prompt.DefaultValue(current);
        }

        var answered = console.Prompt(prompt);
        return string.IsNullOrWhiteSpace(answered) ? current.Trim() : answered.Trim();
    }

    private static string PromptHttpUrl(IAnsiConsole console, string label, bool required, string? current = null)
    {
        var kept = current?.Trim() ?? string.Empty;
        var prompt = new TextPrompt<string>($"{label}:")
            .AllowEmpty()
            .Validate(raw =>
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    if (!string.IsNullOrEmpty(kept))
                    {
                        return ValidationResult.Success();
                    }

                    return required
                        ? ValidationResult.Error("[red]an absolute http(s) URL is required[/]")
                        : ValidationResult.Success();
                }

                return AbsoluteHttpUriAttribute.IsAbsoluteHttpUri(raw.Trim())
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]must be an absolute http(s) URL[/]");
            });
        if (!string.IsNullOrEmpty(kept))
        {
            prompt.DefaultValue(kept);
        }

        var answered = console.Prompt(prompt);
        if (string.IsNullOrWhiteSpace(answered))
        {
            return kept;
        }

        return answered.Trim();
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
        var images = config.Deployment.Update.Bootstrapper.Channel.ToWireValue() + " images";
        return $"{bootstrapper} ({images}, restore the pre-update backup if the health check fails)";
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
        Row("Public API URL", config.Api.OAuth.CallbackBaseUrl);
        if (config.Deployment.IncludeWeb)
        {
            Row("Public Web URL", ConfigPhase.FormatPublicWebOrigin(config));
        }

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

    private static List<string> PromptCsv(IAnsiConsole console, string label, IReadOnlyList<string>? current = null)
    {
        var prompt = new TextPrompt<string>($"{label}:").AllowEmpty();
        if (current is { Count: > 0 })
        {
            prompt.DefaultValue(string.Join(",", current));
        }

        var raw = console.Prompt(prompt);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return current is { Count: > 0 } ? [.. current] : [];
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string PromptKeep(IAnsiConsole console, string label, string current, bool secret)
    {
        var hasCurrent = !string.IsNullOrEmpty(current);
        var prompt = new TextPrompt<string>(
                secret && hasCurrent ? $"{label} (blank keeps the current value):" : $"{label}:")
            .AllowEmpty();
        if (hasCurrent && !secret)
        {
            prompt.DefaultValue(current);
        }

        if (!hasCurrent)
        {
            prompt.Validate(raw => string.IsNullOrWhiteSpace(raw)
                ? ValidationResult.Error("[red]required[/]")
                : ValidationResult.Success());
        }

        if (secret)
        {
            prompt.Secret('*');
        }

        var answered = console.Prompt(prompt);
        if (string.IsNullOrWhiteSpace(answered))
        {
            return hasCurrent ? current : string.Empty;
        }

        return answered.Trim();
    }

    private static bool AskYesNo(IAnsiConsole console, string question, bool fallback) =>
        console.Prompt(new ConfirmationPrompt($"{question}?") { DefaultValue = fallback });

    private static string Choose(IAnsiConsole console, string title, params string[] choices) =>
        console.Prompt(new SelectionPrompt<string>().Title(title).AddChoices(choices));

    /// <summary>Highlights <paramref name="current"/> so Enter keeps the value already in the file.</summary>
    private static string ChooseKeeping(IAnsiConsole console, string title, string current, params string[] choices)
    {
        if (Array.IndexOf(choices, current) > 0)
        {
            choices = [current, .. choices.Where(choice => choice != current)];
        }

        return Choose(console, title, choices);
    }

    private static BootstrapConfig CloneConfig(BootstrapConfig config)
    {
        var json = JsonSerializer.Serialize(config, BootstrapJsonContext.Default.BootstrapConfig);
        return JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.BootstrapConfig)
            ?? new BootstrapConfig();
    }

    private readonly record struct DerivedInputs(
        string Hosts,
        EdgeRoutingMode Mode,
        string ApiHost,
        string WebHost,
        int Http,
        int Https,
        EdgeTlsMode Tls,
        bool Cloudflare)
    {
        public static DerivedInputs Capture(BootstrapConfig config) => new(
            string.Join('\n', config.Edge.Hosts),
            config.Edge.Routing.Mode,
            config.Edge.Routing.ApiHost,
            config.Edge.Routing.WebHost,
            config.Edge.Ports.Http,
            config.Edge.Ports.Https,
            config.Edge.TlsMode,
            config.Edge.Cloudflare.Enabled);
    }

    private static void ClearDerivedIfChanged(DerivedInputs before, BootstrapConfig config)
    {
        if (before == DerivedInputs.Capture(config))
        {
            return;
        }

        config.Api.OAuth.CallbackBaseUrl = string.Empty;
        config.Api.OAuth.JwtAuthority = string.Empty;
        config.Api.CorsAllowedOrigins = [];
    }

    private static bool FirebaseConfigured(FirebaseSection section) =>
        !string.IsNullOrEmpty(section.AndroidConfigPath)
        || !string.IsNullOrEmpty(section.IosConfigPath)
        || !string.IsNullOrEmpty(section.WebConfigPath)
        || !string.IsNullOrEmpty(section.ServiceAccountPath);

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
