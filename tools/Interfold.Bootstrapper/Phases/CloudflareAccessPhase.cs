using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Spectre.Console;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Cloudflare Access (Zero Trust) on public tunnel hostnames: Google IdP, optional Discord
/// OIDC IdP, allowlist, health bypass, service token. Runs during publish so team domain + AUD
/// land in compose env on first boot.
/// </summary>
internal static class CloudflareAccessPhase
{
    internal const string GoogleIdpName = "interfold-google";
    internal const string DiscordIdpName = "interfold-discord";
    internal const string DiscordWorkerName = "interfold-discord-oidc";
    internal const string DiscordKvNamespaceTitle = "interfold-discord-oidc-keys";
    internal const string ServiceTokenName = "interfold-bootstrap";
    internal const string AllowPolicyName = "interfold-allow";
    internal const string ServiceTokenPolicyName = "interfold-service-token";
    internal const string HealthBypassPolicyName = "interfold-health-bypass";

    internal const string LoginMethodsPath = "/auth/login-methods";

    internal static string StatePath(string outputDir)
        => Path.Combine(outputDir, ".cloudflare-access.json");

    internal static string ServiceTokenPath(string outputDir)
        => Path.Combine(outputDir, "secrets", "cloudflare-access-service.token");

    internal static CloudflareAccessState? TryLoadState(string outputDir)
    {
        var path = StatePath(outputDir);
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.CloudflareAccessState);
    }

    internal static CloudflareAccessServiceToken? TryLoadServiceToken(string outputDir)
    {
        var path = ServiceTokenPath(outputDir);
        if (!File.Exists(path))
            return null;

        var lines = File.ReadAllLines(path);
        if (lines.Length < 2
            || string.IsNullOrWhiteSpace(lines[0])
            || string.IsNullOrWhiteSpace(lines[1]))
        {
            return null;
        }

        return new CloudflareAccessServiceToken(string.Empty, lines[0].Trim(), lines[1].Trim());
    }

    internal static async Task EnsureAccessArtifactsAsync(
        BootstrapConfig config,
        string outputDir,
        PhaseLogger logger,
        CancellationToken ct)
    {
        if (!config.Edge.Cloudflare.Access.Enabled)
            return;

        var tunnel = LoadTunnelState(outputDir)
            ?? throw new InvalidOperationException(
                "Cloudflare tunnel state missing; Access requires the tunnel account id from publish.");

        using var client = CloudflareTunnelClient.Create(config.Edge.Cloudflare.ApiToken);
        await EnsureCoreAsync(client, config, tunnel.AccountId, outputDir, logger, confirm: false, ct)
            .ConfigureAwait(false);
    }

    internal static async Task RunAfterTunnelAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        PhaseLogger logger,
        CancellationToken ct)
    {
        if (!config.Edge.Cloudflare.Access.Enabled)
            return;

        var tunnel = LoadTunnelState(options.OutputDir)
            ?? throw new InvalidOperationException(
                "Cloudflare tunnel state missing; cannot configure Access.");

        using var client = CloudflareTunnelClient.Create(config.Edge.Cloudflare.ApiToken);
        await EnsureCoreAsync(client, config, tunnel.AccountId, options.OutputDir, logger, confirm: !options.NonInteractive, ct)
            .ConfigureAwait(false);
    }

    internal static string BuildGoogleCallbackUri(string teamDomain)
        => CloudflareTunnelClient.GoogleAccessCallbackUri(teamDomain);

    private static async Task EnsureCoreAsync(
        CloudflareTunnelClient client,
        BootstrapConfig config,
        string accountId,
        string outputDir,
        PhaseLogger logger,
        bool confirm,
        CancellationToken ct)
    {
        var org = await client.GetOrganizationAsync(accountId, ct).ConfigureAwait(false);
        var teamDomain = CloudflareTunnelClient.NormalizeTeamHost(org.AuthDomain);
        var callbackUri = BuildGoogleCallbackUri(teamDomain);
        logger.Info($"    cloudflare Access team={teamDomain}");
        logger.Info($"    add this OAuth redirect URI on Google (and Discord, when used): {callbackUri}");

        var hostnames = CloudflareTunnelPhase.ResolvePublicHostnames(config);
        if (hostnames.Count == 0)
        {
            throw new InvalidOperationException("No DNS hostnames available for Cloudflare Access apps.");
        }

        var emails = config.Edge.Cloudflare.Access.AllowedEmails;
        var domains = config.Edge.Cloudflare.Access.AllowedEmailDomains;

        var discordReady = HasDiscordOAuth(config.Api.OAuth);
        if (confirm)
        {
            var table = new Table().AddColumn("Hostname").AddColumn("IdP").AddColumn("Allowlist");
            var allowlist = string.Join(", ", emails.Concat(domains.Select(d => $"@{d}")));
            var idpLabel = discordReady ? $"{GoogleIdpName}, {DiscordIdpName}" : GoogleIdpName;
            foreach (var host in hostnames)
                table.AddRow(host, idpLabel, allowlist);
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[yellow]Access callback URI (add on Google and Discord OAuth clients):[/] {callbackUri}");
            if (!AnsiConsole.Confirm("Apply Cloudflare Access apps and policies?", defaultValue: true))
            {
                logger.Warn("operator declined Cloudflare Access configuration");
                return;
            }
        }

        var googleIdpId = await client.EnsureGoogleIdentityProviderAsync(
                accountId,
                config.Api.OAuth.GoogleClientId.Trim(),
                config.Api.OAuth.GoogleClientSecret.Trim(),
                ct)
            .ConfigureAwait(false);
        logger.Info($"    cloudflare Access Google IdP id={googleIdpId} ({GoogleIdpName})");

        var discord = await TryEnsureDiscordAccessAsync(
                client, config, accountId, outputDir, callbackUri, logger, ct)
            .ConfigureAwait(false);
        var allowedIdps = discord is null ? new[] { googleIdpId } : [googleIdpId, discord.IdpId];
        var autoRedirect = discord is null;

        var priorToken = TryLoadServiceToken(outputDir);
        var serviceToken = await client.EnsureServiceTokenAsync(
                accountId,
                ServiceTokenName,
                priorToken?.ClientId,
                priorToken?.ClientSecret,
                ct)
            .ConfigureAwait(false);
        await PersistServiceTokenAsync(outputDir, serviceToken, logger, ct).ConfigureAwait(false);

        var appIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? primaryAud = null;
        var allowPolicy = BuildAllowPolicy(emails, domains);
        var servicePolicy = new CloudflareAccessPolicySpec(
            ServiceTokenPolicyName,
            "non_identity",
            [new CloudflareAccessPolicyRule(CloudflareAccessPolicyRuleKind.ServiceToken, serviceToken.Id)],
            []);
        var bypassPolicy = new CloudflareAccessPolicySpec(
            HealthBypassPolicyName,
            "bypass",
            [new CloudflareAccessPolicyRule(CloudflareAccessPolicyRuleKind.Everyone)],
            []);

        var apiHost = hostnames[0];
        foreach (var host in hostnames)
        {
            var isApiHost = string.Equals(host, apiHost, StringComparison.OrdinalIgnoreCase);
            var app = await client.EnsureSelfHostedAppAsync(
                    accountId, host, allowedIdps, autoRedirect, ct, optionsPreflightBypass: isApiHost)
                .ConfigureAwait(false);
            appIds[host] = app.Id;
            primaryAud ??= app.Aud;
            logger.Info($"    cloudflare Access app {host} id={app.Id} aud={app.Aud}");
            await client.ReplaceAppPoliciesAsync(accountId, app.Id, [allowPolicy, servicePolicy], ct)
                .ConfigureAwait(false);

            // A cross-origin fetch cannot follow the Access login redirect. login-methods is
            // anonymous discovery, so it has to reach the API the same way health does.
            var bypassPaths = new List<string> { $"{host}/health", $"{host}/health/ready" };
            if (isApiHost)
                bypassPaths.Add($"{host}{LoginMethodsPath}");

            foreach (var path in bypassPaths)
            {
                var healthApp = await client.EnsureSelfHostedAppAsync(accountId, path, allowedIdps, autoRedirect, ct)
                    .ConfigureAwait(false);
                appIds[path] = healthApp.Id;
                await client.ReplaceAppPoliciesAsync(accountId, healthApp.Id, [bypassPolicy], ct)
                    .ConfigureAwait(false);
            }
        }

        if (string.IsNullOrWhiteSpace(primaryAud))
            throw new InvalidOperationException("Cloudflare Access app create/update did not return an AUD.");

        var state = new CloudflareAccessState
        {
            TeamDomain = teamDomain,
            Aud = primaryAud,
            IdentityProviderId = googleIdpId,
            DiscordIdentityProviderId = discord?.IdpId ?? string.Empty,
            DiscordWorkerName = discord is null ? string.Empty : DiscordWorkerName,
            DiscordKvNamespaceId = discord?.KvNamespaceId ?? string.Empty,
            DiscordWorkerUrl = discord?.WorkerUrl ?? string.Empty,
            AppIds = appIds,
        };
        var json = JsonSerializer.Serialize(state, BootstrapJsonContext.Default.CloudflareAccessState);
        await File.WriteAllTextAsync(StatePath(outputDir), json, ct).ConfigureAwait(false);
        logger.Info($"    persisted Access state team={teamDomain} aud={primaryAud}");
        logger.Info($"    Access callback URI: {callbackUri}");
    }

    internal static bool HasDiscordOAuth(ApiOAuthSection oauth)
        => !string.IsNullOrWhiteSpace(oauth.DiscordClientId)
           && !string.IsNullOrWhiteSpace(oauth.DiscordClientSecret);

    internal static CloudflareAccessPolicySpec BuildAllowPolicy(
        IReadOnlyList<string> emails,
        IReadOnlyList<string> domains)
    {
        var include = new List<CloudflareAccessPolicyRule>();
        foreach (var email in emails)
        {
            if (!string.IsNullOrWhiteSpace(email))
                include.Add(new CloudflareAccessPolicyRule(CloudflareAccessPolicyRuleKind.Email, email.Trim()));
        }

        foreach (var domain in domains)
        {
            if (!string.IsNullOrWhiteSpace(domain))
                include.Add(new CloudflareAccessPolicyRule(CloudflareAccessPolicyRuleKind.EmailDomain, domain.Trim()));
        }

        return new CloudflareAccessPolicySpec(AllowPolicyName, "allow", include, []);
    }

    private static async Task<DiscordAccessArtifacts?> TryEnsureDiscordAccessAsync(
        CloudflareTunnelClient client,
        BootstrapConfig config,
        string accountId,
        string outputDir,
        string callbackUri,
        PhaseLogger logger,
        CancellationToken ct)
    {
        if (!HasDiscordOAuth(config.Api.OAuth))
        {
            logger.Info("    discord Access skipped (api.oauth.discordClientId/secret not both set)");
            return null;
        }

        try
        {
            using var http = DiscordOidcWorkerSource.CreateHttp();
            var workDir = await DiscordOidcWorkerSource.EnsureWorkDirectoryAsync(outputDir, http, logger, ct)
                .ConfigureAwait(false);
            DiscordOidcWorkerSource.WriteConfig(
                workDir,
                config.Api.OAuth.DiscordClientId.Trim(),
                config.Api.OAuth.DiscordClientSecret.Trim(),
                callbackUri);
            var bundlePath = await DiscordOidcWorkerBundler.BundleAsync(workDir, logger, ct)
                .ConfigureAwait(false);
            var configPath = Path.Combine(workDir, DiscordOidcWorkerSource.ConfigFileName);

            var kvId = await client.EnsureKvNamespaceAsync(accountId, DiscordKvNamespaceTitle, ct)
                .ConfigureAwait(false);
            var subdomain = await client.EnsureWorkersSubdomainAsync(accountId, ct)
                .ConfigureAwait(false);
            var workerUrl = await client.EnsureDiscordOidcWorkerAsync(
                    accountId,
                    DiscordWorkerName,
                    bundlePath,
                    configPath,
                    kvId,
                    subdomain,
                    ct)
                .ConfigureAwait(false);
            var idpId = await client.EnsureOidcIdentityProviderAsync(
                    accountId,
                    DiscordIdpName,
                    config.Api.OAuth.DiscordClientId.Trim(),
                    config.Api.OAuth.DiscordClientSecret.Trim(),
                    workerUrl,
                    ct)
                .ConfigureAwait(false);
            logger.Info($"    cloudflare Access Discord IdP id={idpId} ({DiscordIdpName}) url={workerUrl}");
            return new DiscordAccessArtifacts(idpId, kvId, workerUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warn($"discord Access not applied; Google Access left in place: {ex.Message}");
            return null;
        }
    }

    private sealed record DiscordAccessArtifacts(string IdpId, string KvNamespaceId, string WorkerUrl);

    private static async Task PersistServiceTokenAsync(
        string outputDir,
        CloudflareAccessServiceToken token,
        PhaseLogger logger,
        CancellationToken ct)
    {
        var path = ServiceTokenPath(outputDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $"{token.ClientId}{Environment.NewLine}{token.ClientSecret}{Environment.NewLine}", ct)
            .ConfigureAwait(false);
        UnixFilePermissions.SetOwnerOnly(path, logger, "cloudflare access service token");
    }

    private static CloudflareTunnelState? LoadTunnelState(string outputDir)
    {
        var path = CloudflareTunnelPhase.StatePath(outputDir);
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize(File.ReadAllText(path), BootstrapJsonContext.Default.CloudflareTunnelState);
    }
}
