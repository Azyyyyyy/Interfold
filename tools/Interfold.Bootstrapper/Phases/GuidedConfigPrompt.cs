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
    internal const string ReachabilityCloudflare = "Public internet (Cloudflare)";
    internal const string ReachabilityHint =
        "The internet choice only works when Cloudflare looks up your web address.\nFor example, home.example.com.\nIf Cloudflare does not look that name up, use the local network instead.\nAn address like 192.168.1.42 also uses the local network.";

    internal const string HostQuestionLan = "What address should people use?";
    internal const string HostHintLan =
        "A name like home.example.com, or this computer's address, such as 192.168.1.42. Separate more than one with a comma.";
    internal const string HostQuestionCloudflare = "What web address should people use?";
    internal const string HostHintCloudflare =
        "A name Cloudflare looks up for you, like home.example.com. Separate more than one with a comma.";

    internal const string TrustQuestion = "Let this computer trust Interfold's certificate";
    internal const string TrustHint =
        "Browsers warn that a home-network site is unsafe until this computer accepts Interfold's certificate. Yes stops that warning here. Phones and other computers still need the certificate installed on them.";

    internal const string RoutingTitle = "One web address, or two?";
    internal const string RoutingPath = "One web address";
    internal const string RoutingSeparate = "Two web addresses (recommended)";

    internal const string SignInTitle = "Sign-in";
    internal const string SignInGoogle = "Google";
    internal const string SignInDiscord = "Discord";
    internal const string SignInApple = "Apple";
    internal static readonly string[] SignInHint =
    [
        "Ticked options are shown when someone signs in.",
        "Unticked options stay hidden.",
    ];

    internal const string SignInInstructions =
        "(Press space to tick or untick, then Enter. Leave them all unticked to skip sign-in.)";
    internal const string SignInInstructionsAccess =
        "(Press space to tick or untick, then Enter. Cloudflare Access needs Google or Discord.)";
    internal const string SignInInstructionsAccessDiscord =
        "(Press space to tick or untick, then Enter. Cloudflare Access needs Discord.)";
    internal const string AccessNeedsProvider = "Cloudflare Access needs Google or Discord sign-in.";
    internal const string AccessNeedsDiscord = "Cloudflare Access needs Discord sign-in for this address.";

    internal const string GoogleClientIdLabel = "Google OAuth client ID";
    internal const string DiscordClientIdLabel = "Discord OAuth client ID";
    internal const string AppleClientIdLabel = "Apple OAuth client ID";

    internal const string GoogleCredentialsPage = "https://console.cloud.google.com/apis/credentials";
    internal const string DiscordApplicationsPage = "https://discord.com/developers/applications";

    internal static readonly string[] GoogleCredentialHelp =
    [
        "In Google Cloud Console, create a new project to hold these credentials.",
        "Open this page:",
        GoogleCredentialsPage,
        "Choose Create credentials, then OAuth client ID, then Web application.",
        "If Google asks for a consent screen first, finish that and come back.",
        "Copy the client ID and client secret.",
        "Google sign-in needs a public domain name, such as example.com.",
        "A name ending in .local will not work.",
    ];

    internal static readonly string[] DiscordCredentialHelp =
    [
        "Open this page:",
        DiscordApplicationsPage,
        "Choose New Application, then OAuth2.",
        "Copy the Client ID and Client Secret.",
    ];

    internal const string AppleCredentialHelp =
        "In your Apple Developer account, create a Services ID and turn on Sign in with Apple. That Services ID is the client ID. Apple does not show a secret to copy. Create a Sign in with Apple key, then make a client secret from your Team ID, Key ID, Services ID, and that key. The secret lasts at most six months, then you make a new one.";

    internal static readonly string[] AccessRedirectHelp =
    [
        "Also add this address, using your Cloudflare Zero Trust team name:",
        "https://<team>.cloudflareaccess.com/cdn-cgi/access/callback",
    ];
    internal const string GoogleUnavailableWarning =
        "Google sign-in is not available for this address. Google needs a public domain name, such as example.com. A name ending in .local, or a numeric address, will not work.";
    internal const string AccessUsesDiscord =
        "Cloudflare Access can use Discord sign-in instead.";
    internal const string CloudflareTokenPage = "https://dash.cloudflare.com/profile/api-tokens";

    internal static readonly string[] CloudflareCorePermissions =
    [
        "Account → Cloudflare Tunnel → Edit",
        "Zone → Zone → Edit",
        "Zone → DNS → Edit",
        "Zone → SSL and Certificates → Edit",
        "Account → Access: Apps and Policies → Edit",
        "Account → Access: Service Tokens → Edit",
        "Account → Organization → Read",
    ];

    internal static readonly string[] CloudflareDiscordPermissions =
    [
        "Account → Workers Scripts → Edit",
        "Account → Workers KV Storage → Edit",
    ];
    internal const string PortsQuestion = "Use the standard web ports (80 and 443)";

    internal const string SummaryConfirm = "Confirm and continue";
    internal const string SummaryStartOver = "Start over";
    internal const string SummaryAdvanced = "Open advanced editor";

    internal const string UpdatesUnavailableMessage =
        "Automatic updates are not available until scheduled backups are turned on. Updates run after each successful backup.";

    internal const string UpdateQuestion = "Install updates after each successful backup";
    internal const string BootstrapperUpdateQuestion = "Also update the bootstrapper before the container images";
    internal const string FirebaseQuestion = "Set up Firebase push notifications";
    internal const string FirebaseConsolePage = "https://console.firebase.google.com/";
    internal const string OtlpCollectorDocs = "https://opentelemetry.io/docs/collector/";

    internal static readonly string[] FirebaseHintIntro =
    [
        "Firebase tells phones and browsers when who is fronting changes for a system.",
        "No turns those alerts off. The rest of Interfold still works.",
        "",
    ];

    internal static readonly string[] FirebaseHintWithGoogle =
    [
        "Use the same Google Cloud project you created for Google sign-in.",
        "Open this page:",
        FirebaseConsolePage,
        "Choose Add project. If the button says Create a project, choose that.",
        "On the first step, pick the Google Cloud project you use for sign-in.",
        "You can turn Google Analytics off, then finish.",
        "",
    ];

    internal static readonly string[] FirebaseHintWithoutGoogle =
    [
        "Open this page:",
        FirebaseConsolePage,
        "Choose Add project. If the button says Create a project, choose that.",
        "On the first step, type a name for the project.",
        "You can turn Google Analytics off, then finish.",
        "",
    ];

    internal static readonly string[] FirebaseHintFiles =
    [
        "Open the project.",
        "Choose the gear next to Project Overview, then Project settings.",
        "",
        "Under General, then Your apps, add each app you ship and download its file:",
        "  Android: google-services.json",
        "  iOS: GoogleService-Info.plist",
        "  Web: copy only the firebaseConfig block and save it as a file.",
        "  It looks like const firebaseConfig = { ... };",
        "  Leave out the import lines above it.",
        "",
        "Still in Project settings, open Cloud Messaging.",
        "Under Web Push certificates, copy the key pair.",
        "Interfold asks for that key and saves it with the config.",
        "",
        "Still in Project settings, open Service accounts.",
        "Choose Generate new private key and save that file.",
        "Interfold uses it to send the alerts.",
        "",
        "Put the files in one folder. Yes asks for that folder, or for each file.",
    ];

    internal const string OtlpQuestion = "Export telemetry to an OpenTelemetry collector";

    internal static readonly string[] OtlpHint =
    [
        "Most people should leave this off.",
        "Interfold does not need it.",
        "Turn it on only when you want full logging from the API, and from the app.",
        "",
        "You need an OpenTelemetry collector that is already running.",
        "A collector is a separate program.",
        "It receives those logs and forwards them to a dashboard you choose.",
        "OpenTelemetry explains how to run one here:",
        OtlpCollectorDocs,
        "",
        "Yes asks for that collector's address.",
        "The API sends its logs, traces, and performance numbers.",
        "The app sends its own only if you then advertise the collector to clients.",
    ];

    internal static readonly string[] OtlpAdvertiseHint =
    [
        "Yes tells each client where to send its own logs.",
        "A client can use the same collector as the API, or a different one.",
        "The next question asks for that address.",
        "Leave it blank to use the API's collector.",
        "No keeps logging on the API only.",
    ];
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
            $"[bold]{ReachabilityTitle}[/]\n[grey]{ReachabilityHint}[/]",
            config.Edge.Cloudflare.Enabled ? ReachabilityCloudflare : ReachabilityLan,
            ReachabilityLan,
            ReachabilityCloudflare);
        var lan = reach == ReachabilityLan;

        // Scylla and Postgres stay in the advanced editor. Guided always uses SQLite.
        config.Datastores.Persistence = PersistenceMode.Sqlite;
        var includeWeb = AskYesNo(console, "Include the web UI (interfold-web)", config.Deployment.IncludeWeb);

        var seed = SeedForPrompt(config.Edge.Hosts, localAddressProbe, hostnameProbe, dnsOnly: !lan);
        console.MarkupLine($"[grey]{(lan ? HostHintLan : HostHintCloudflare)}[/]");
        var hosts = PromptHosts(
            console,
            lan ? HostQuestionLan : HostQuestionCloudflare,
            seed,
            dnsOnly: !lan);

        config.Deployment.IncludeWeb = includeWeb;
        config.Edge.Hosts = hosts;

        var anyDns = hosts.Any(IsDns);
        if (includeWeb && anyDns)
        {
            // A new file highlights two addresses. Reconfigure still keeps the saved choice on Enter.
            var routingCurrent = existing is not null && config.Edge.Routing.Mode != EdgeRoutingMode.Subdomain
                ? RoutingPath
                : RoutingSeparate;
            var baseHost = PrimaryDnsHost(hosts);
            var routing = ChooseKeeping(
                console,
                $"[bold]{RoutingTitle}[/]\n[grey]One address keeps the website and the API on {Markup.Escape(baseHost)}. Two addresses puts the API at api.{Markup.Escape(baseHost)} and the website at web.{Markup.Escape(baseHost)}.[/]",
                routingCurrent,
                RoutingSeparate,
                RoutingPath);
            if (routing == RoutingSeparate)
            {
                config.Edge.Routing.Mode = EdgeRoutingMode.Subdomain;
                console.MarkupLine(
                    $"[grey]These names are added in front of {Markup.Escape(baseHost)}. A full name is kept when it already ends with {Markup.Escape(baseHost)}.[/]");
                config.Edge.Routing.ApiHost = PromptNameInFrontOfHost(
                    console, "API", baseHost, config.Edge.Routing.ApiHost, suggested: "api", otherFull: null);
                config.Edge.Routing.WebHost = PromptNameInFrontOfHost(
                    console, "Website", baseHost, config.Edge.Routing.WebHost, suggested: "web", otherFull: config.Edge.Routing.ApiHost);
            }
            else
            {
                config.Edge.Routing.Mode = EdgeRoutingMode.Path;
            }
        }
        else if (!anyDns)
        {
            // A numeric address cannot parent the saved subdomain pair. Leaving that
            // pair on makes the public origin a DNS name, so Google stays on the list.
            config.Edge.Routing.Mode = EdgeRoutingMode.Path;
        }

        if (lan)
        {
            config.Edge.TlsMode = EdgeTlsMode.PrivateCa;
            // Access is only collected on the tunnel path. Leaving it on skips sign-in.
            config.Edge.Cloudflare.Enabled = false;
            config.Edge.Cloudflare.Access.Enabled = false;
            console.MarkupLine($"[grey]{TrustHint}[/]");
            config.Edge.Certificates.TrustStoreInstall = AskYesNo(
                console,
                TrustQuestion,
                config.Edge.Certificates.TrustStoreInstall);
            // Cloudflare does not publish host ports, so this question is LAN-only.
            PromptPublishedPorts(console, config);
        }
        else
        {
            // ValidateEdge coerces tlsMode to none once the tunnel is on.
            config.Edge.Cloudflare.Enabled = true;
            ExplainCloudflareToken(console);
            config.Edge.Cloudflare.ApiToken = PromptKeep(
                console, "Cloudflare API token", config.Edge.Cloudflare.ApiToken, maskSecrets);
            var access = config.Edge.Cloudflare.Access;
            access.Enabled = AskYesNo(
                console, "Restrict who can open the site with Cloudflare Access", access.Enabled);
            if (access.Enabled)
            {
                PromptAccessAllowlist(console, access);
            }
        }

        PromptSignIn(console, config, maskSecrets);

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
                ? ValidationResult.Error("[red]Type the address people will use.[/]")
                : ValidationResult.Success();
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return ValidationResult.Error("[red]Type the address people will use.[/]");
        }

        var parsed = new List<HostEntry>(parts.Length);
        foreach (var part in parts)
        {
            try
            {
                parsed.Add(HostParser.Parse(part));
            }
            catch (FormatException)
            {
                return ValidationResult.Error(
                    "[red]Use a name like home.example.com, or an address like 192.168.1.42.[/]");
            }
        }

        if (dnsOnly && parsed.Any(entry => entry.Kind != HostKind.Dns))
        {
            return ValidationResult.Error(
                "[red]Use a name like home.example.com. A numeric address won't work with Cloudflare.[/]");
        }

        if (!parsed.Any(entry => entry.IsLeafEligible))
        {
            return ValidationResult.Error(
                "[red]Use a name or a single address, such as 192.168.1.42.[/]");
        }

        return ValidationResult.Success();
    }

    private static string PrimaryDnsHost(IReadOnlyList<string> hosts)
    {
        foreach (var raw in hosts)
        {
            try
            {
                var entry = HostParser.Parse(raw);
                if (entry.Kind == HostKind.Dns)
                {
                    return entry.DnsName ?? raw.Trim().TrimEnd('.');
                }
            }
            catch (FormatException)
            {
            }
        }

        return hosts[0].Trim().TrimEnd('.');
    }

    private static string? LabelInFrontOf(string full, string baseHost)
    {
        var suffix = "." + baseHost;
        if (full.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && full.Length > suffix.Length)
        {
            return full[..^suffix.Length];
        }

        return null;
    }

    private static string PromptNameInFrontOfHost(
        IAnsiConsole console,
        string role,
        string baseHost,
        string currentFull,
        string suggested,
        string? otherFull)
    {
        var fallback = LabelInFrontOf(currentFull, baseHost) ?? suggested;
        var prompt = new TextPrompt<string>($"{role} name (in front of {baseHost}, or the full name):")
            .DefaultValue(fallback)
            .AllowEmpty()
            .ValidationErrorMessage($"[red]{HostNameError(baseHost)}[/]")
            .Validate(raw =>
            {
                var label = string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim().Trim('.');
                if (!TryComposeHost(label, baseHost, out var full, out var error))
                {
                    return ValidationResult.Error($"[red]{error}[/]");
                }

                return otherFull is not null && full.Equals(otherFull, StringComparison.OrdinalIgnoreCase)
                    ? ValidationResult.Error("[red]API and web need different names[/]")
                    : ValidationResult.Success();
            });
        var answered = console.Prompt(prompt);
        var chosen = string.IsNullOrWhiteSpace(answered) ? fallback : answered.Trim().Trim('.');
        TryComposeHost(chosen, baseHost, out var composed, out _);
        return composed;
    }

    private static string HostNameError(string baseHost) =>
        $"must be a name in front of {baseHost}, or the full name under it";

    private static bool TryComposeHost(string label, string baseHost, out string full, out string error)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(label)
            || label.Contains(' ')
            || label.Contains(',')
            || label.Contains(".."))
        {
            error = HostNameError(baseHost);
            return false;
        }

        // The bare host is the parent, not an API or web name. A name that already
        // ends with it is the finished hostname; anything else is a prefix.
        var suffix = "." + baseHost;
        if (label.Equals(baseHost, StringComparison.OrdinalIgnoreCase))
        {
            error = HostNameError(baseHost);
            return false;
        }

        full = label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? label
            : $"{label}.{baseHost}";
        try
        {
            var entry = HostParser.Parse(full);
            if (entry.Kind != HostKind.Dns)
            {
                error = HostNameError(baseHost);
                return false;
            }
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void PromptAccessAllowlist(IAnsiConsole console, EdgeCloudflareAccessSection access)
    {
        while (true)
        {
            var emails = PromptCsv(
                console, "Access allowed emails (comma-separated, blank to skip)", access.AllowedEmails);
            console.MarkupLine("[grey]A domain lets everyone at that name sign in.[/]");
            console.MarkupLine("[grey]For example, example.com allows ada@example.com.[/]");
            var domains = PromptCsv(
                console, "Access allowed email domains, such as example.com (comma-separated, blank to skip)", access.AllowedEmailDomains);
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
        var oauth = config.Api.OAuth;
        var googleAllowed = GoogleSignInAllowed(config);
        if (!googleAllowed)
        {
            WarnGoogleUnavailable(console);
            oauth.GoogleClientId = string.Empty;
            oauth.GoogleClientSecret = string.Empty;
        }

        var accessOn = config.Edge.Cloudflare.Access.Enabled;
        if (accessOn && !googleAllowed)
        {
            console.MarkupLine($"[grey]{AccessUsesDiscord}[/]");
        }

        var choices = new List<string>(3);
        if (googleAllowed)
        {
            choices.Add(SignInGoogle);
        }

        choices.Add(SignInDiscord);
        choices.Add(SignInApple);

        // Apple alone cannot satisfy Access. Empty is valid only when Access is off.
        var instructions = accessOn
            ? googleAllowed ? SignInInstructionsAccess : SignInInstructionsAccessDiscord
            : SignInInstructions;
        var preselected = new HashSet<string>(StringComparer.Ordinal);
        if (googleAllowed && !string.IsNullOrEmpty(oauth.GoogleClientId))
        {
            preselected.Add(SignInGoogle);
        }

        if (!string.IsNullOrEmpty(oauth.DiscordClientId) || (accessOn && !googleAllowed))
        {
            preselected.Add(SignInDiscord);
        }

        if (!string.IsNullOrEmpty(oauth.AppleClientId))
        {
            preselected.Add(SignInApple);
        }

        WriteGreyLines(console, SignInHint);

        List<string> selected;
        while (true)
        {
            var prompt = new MultiSelectionPrompt<string>()
                .Title($"[bold]{SignInTitle}[/]")
                .NotRequired()
                .InstructionsText($"[grey]{instructions}[/]")
                .AddChoices(choices);
            foreach (var choice in preselected)
            {
                prompt.Select(choice);
            }

            selected = console.Prompt(prompt);
            if (!accessOn || selected.Contains(SignInGoogle) || selected.Contains(SignInDiscord))
            {
                break;
            }

            console.MarkupLine($"[red]{(googleAllowed ? AccessNeedsProvider : AccessNeedsDiscord)}[/]");
            preselected = selected.ToHashSet(StringComparer.Ordinal);
        }

        var accessRedirect = accessOn ? AccessRedirectHelp : null;
        ApplyProvider(
            console, config, maskSecrets, selected.Contains(SignInGoogle),
            "google", GoogleCredentialHelp, accessRedirect,
            oauth.GoogleClientId, oauth.GoogleClientSecret,
            GoogleClientIdLabel, "Google OAuth client secret",
            value => oauth.GoogleClientId = value,
            value => oauth.GoogleClientSecret = value);
        ApplyProvider(
            console, config, maskSecrets, selected.Contains(SignInDiscord),
            "discord", DiscordCredentialHelp, accessRedirect,
            oauth.DiscordClientId, oauth.DiscordClientSecret,
            DiscordClientIdLabel, "Discord OAuth client secret",
            value => oauth.DiscordClientId = value,
            value => oauth.DiscordClientSecret = value);
        ApplyProvider(
            console, config, maskSecrets, selected.Contains(SignInApple),
            "apple", [AppleCredentialHelp], extra: null,
            oauth.AppleClientId, oauth.AppleClientSecret,
            AppleClientIdLabel, "Apple OAuth client secret",
            value => oauth.AppleClientId = value,
            value => oauth.AppleClientSecret = value);
    }

    private static void ApplyProvider(
        IAnsiConsole console,
        BootstrapConfig config,
        bool maskSecrets,
        bool selected,
        string provider,
        IEnumerable<string> howTo,
        IEnumerable<string>? extra,
        string currentId,
        string currentSecret,
        string idLabel,
        string secretLabel,
        Action<string> setId,
        Action<string> setSecret)
    {
        if (!selected)
        {
            setId(string.Empty);
            setSecret(string.Empty);
            return;
        }

        ExplainProvider(console, config, provider, howTo, extra);
        setId(PromptKeep(console, idLabel, currentId, secret: false));
        setSecret(PromptKeep(console, secretLabel, currentSecret, maskSecrets));
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
                new TextPrompt<int>("How many backup archives should be kept?")
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
        ExplainFirebase(console, googleSignIn: !string.IsNullOrEmpty(config.Api.OAuth.GoogleClientId));
        if (AskYesNo(console, FirebaseQuestion, firebaseWasOn))
        {
            ConfigPhase.PromptGuidedFirebase(console, config.Api.Firebase);
        }
        else
        {
            config.Api.Firebase.AndroidConfigPath = string.Empty;
            config.Api.Firebase.IosConfigPath = string.Empty;
            config.Api.Firebase.WebConfigPath = string.Empty;
            config.Api.Firebase.WebPushKey = string.Empty;
            config.Api.Firebase.ServiceAccountPath = string.Empty;
        }

        var otlpWasOn = !string.IsNullOrWhiteSpace(config.Observability.OtlpEndpoint);
        WriteGreyLines(console, OtlpHint);
        if (!AskYesNo(console, OtlpQuestion, otlpWasOn))
        {
            config.Observability.OtlpEndpoint = string.Empty;
            config.Observability.AdvertiseOtlpToClients = false;
            config.Observability.ClientOtlpHttpEndpoint = string.Empty;
            return;
        }

        config.Observability.OtlpEndpoint = PromptHttpUrl(
            console, "OTLP collector URL", required: true, config.Observability.OtlpEndpoint);
        WriteGreyLines(console, OtlpAdvertiseHint);
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
            : ReachabilityCloudflare);
        Row("Host", string.Join(", ", config.Edge.Hosts));
        Row("Web UI", config.Deployment.IncludeWeb ? "yes" : "no");
        Row("Routing", DescribeRouting(config, ipOnly));
        Row("Persistence", DescribePersistence(config));
        if (lan)
        {
            Row("Certificate", config.Edge.Certificates.TrustStoreInstall
                ? "trusted on this computer"
                : "not installed on this computer");
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
        // Path mode serves the API at /api/ and the site at /. The callback origin stays
        // unpathed; these rows are the addresses people open.
        var showPath = config.Edge.Routing.Mode == EdgeRoutingMode.Path;
        Row("Public API URL", showPath
            ? WithPublicPath(config.Api.OAuth.CallbackBaseUrl, "/api/")
            : config.Api.OAuth.CallbackBaseUrl);
        if (config.Deployment.IncludeWeb)
        {
            var webUrl = ConfigPhase.FormatPublicWebOrigin(config);
            Row("Public Web URL", showPath ? WithPublicPath(webUrl, "/") : webUrl);
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

    private static string WithPublicPath(string origin, string path)
    {
        if (string.IsNullOrEmpty(origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.AbsolutePath is not ("/" or ""))
        {
            return origin;
        }

        return origin.TrimEnd('/') + path;
    }

    private static string DescribeRouting(BootstrapConfig config, bool ipOnly)
    {
        if (config.Edge.Routing.Mode == EdgeRoutingMode.Subdomain)
        {
            return $"two web addresses ({config.Edge.Routing.ApiHost} and {config.Edge.Routing.WebHost})";
        }

        return ipOnly
            ? "one web address (IP hosts stay on one address)"
            : "one web address";
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

    // Google rejects OAuth redirects that are not a public domain name.
    internal static bool GoogleSignInAllowed(BootstrapConfig config)
    {
        var origin = ConfigPhase.FormatPublicApiOrigin(config);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        try
        {
            var entry = HostParser.Parse(uri.IdnHost);
            if (entry.Kind != HostKind.Dns || string.IsNullOrEmpty(entry.DnsName))
            {
                return false;
            }

            var labels = entry.DnsName.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
            return labels.Length >= 2
                && !labels[^1].Equals("local", StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void WarnGoogleUnavailable(IAnsiConsole console) =>
        console.MarkupLine($"[yellow]{GoogleUnavailableWarning}[/]");

    private static void ExplainCloudflareToken(IAnsiConsole console)
    {
        console.MarkupLine("[grey]Open this page and create a custom token:[/]");
        console.MarkupLine($"[grey]{CloudflareTokenPage}[/]");
        console.MarkupLine("[grey]Choose Create Token, then Create Custom Token.[/]");
        console.WriteLine();
        WritePermissionList(console, "Core permissions:", CloudflareCorePermissions);
        console.WriteLine();
        WritePermissionList(console, "Only if you plan to use Discord to sign in:", CloudflareDiscordPermissions);
        console.WriteLine();
        console.MarkupLine("[grey]Limit the token to one account. Do not use a token that has every permission.[/]");
    }

    private static void ExplainFirebase(IAnsiConsole console, bool googleSignIn)
    {
        WriteGreyLines(console, FirebaseHintIntro);
        WriteGreyLines(console, googleSignIn ? FirebaseHintWithGoogle : FirebaseHintWithoutGoogle);
        WriteGreyLines(console, FirebaseHintFiles);
    }

    private static void WriteGreyLines(IAnsiConsole console, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0)
                console.WriteLine();
            else
                console.MarkupLine($"[grey]{line}[/]");
        }
    }

    private static void WritePermissionList(IAnsiConsole console, string heading, IEnumerable<string> permissions)
    {
        console.MarkupLine($"[grey]{heading}[/]");
        foreach (var permission in permissions)
            console.MarkupLine($"[grey]  {permission}[/]");
    }

    // Login and account-link callbacks. See OAuthControllerBase.BuildCallbackBaseUri.
    private static void ExplainProvider(
        IAnsiConsole console,
        BootstrapConfig config,
        string provider,
        IEnumerable<string> howTo,
        IEnumerable<string>? extra = null)
    {
        WriteGreyLines(console, howTo);
        var origin = ConfigPhase.FormatPublicApiOrigin(config);
        if (!string.IsNullOrEmpty(origin))
        {
            console.WriteLine();
            console.MarkupLine("[grey]Add these addresses as redirect or return URLs:[/]");
            console.MarkupLine($"[grey]{origin}/auth/{provider}/callback[/]");
            console.MarkupLine($"[grey]{origin}/auth/link/{provider}/callback[/]");
        }

        if (extra is not null)
        {
            console.WriteLine();
            WriteGreyLines(console, extra);
        }
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
