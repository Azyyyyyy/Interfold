using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Interfold.Api.Services.Secrets;

/// <summary>
/// Populates the API's <see cref="SecretsSnapshot"/> before <c>WebApplicationBuilder.Build()</c>
/// runs. Unifies what used to be two separate readers:
/// <list type="bullet">
///   <item>The hosted-service <c>SecretsSnapshotLoader</c> (post-Build, ran on
///   <c>IHostedLifecycleService.StartingAsync</c>, read through <see cref="ISecretsStore"/>) that
///   fed <see cref="AuthenticationSecretsPostConfigure"/>, <see cref="FirebaseClientSecretsPostConfigure"/>,
///   and <see cref="FcmSecretsPostConfigure"/>.</item>
///   <item>The bare-Npgsql leaf-PFX-password loader that used to live directly in <c>Program.cs</c>
///   as <c>LoadLeafPfxPasswordFromStoreIfNeeded</c>.</item>
/// </list>
///
/// <para>
/// <b>Persistence-mode branch:</b> when <c>OCTOCON_POSTGRES_CONNECTION</c> is present, every
/// snapshot row (plus the leaf PFX password) is fetched from <c>internal.secrets</c> in a single
/// batched query via a bare <see cref="NpgsqlConnection"/> — <see cref="ISecretsStore"/> isn't
/// constructed yet this early, and building it manually here would duplicate the DI wiring for no
/// benefit. When it's absent (pure InMemory persistence — every InMemory-mode integration test and
/// any InMemory self-hosted deployment), the four mandatory auth secrets are instead read directly
/// off the same <see cref="IConfigurationBuilder"/> from the <c>OCTOCON_INMEMORY_SECRETS_SEED:*</c>
/// keys — the exact env-var family <c>InMemoryServiceCollectionExtensions.AddInMemoryPersistence</c>
/// uses to seed <c>InMemorySecretsStore</c>, just read one layer earlier. OAuth / Firebase-client /
/// FCM rows have no InMemory seed path and stay null in that branch, matching pre-existing
/// behaviour (<c>InMemorySecretsStore</c> never carried them either).
/// </para>
///
/// <para>
/// <b>Ordering:</b> called from <c>Program.cs</c> immediately after <c>builder.AddServiceDefaults()</c>,
/// before any <c>builder.Services.Add...</c> call. The returned <see cref="SecretsSnapshot"/> is
/// registered as an instance singleton (<c>AddSingleton&lt;ISecretsSnapshot&gt;(snapshot)</c>) — valid
/// pre-<c>Build()</c> because it hands the container a pre-built instance rather than asking it to
/// construct one.
/// </para>
///
/// <para>
/// <b>Trade-off:</b> unlike <c>PostgresSecretsStore</c> (which goes through
/// <c>PostgresConnectionFactory</c> and picks up <c>DatabaseTransientRetry</c>'s retry-on-transient
/// policy), this bare connection has no retry — matching the pre-existing leaf-PFX loader's
/// behaviour, which never had retry either. A transient connection blip at boot now fails the
/// whole snapshot fetch instead of just the PFX path.
/// </para>
///
/// <para>
/// <b>Non-pooled by design.</b> The connection this loader opens is forced to
/// <c>Pooling=false</c> (see <see cref="WithPoolingDisabled"/>). Npgsql keys its connection pool
/// on the full connection string, so any consumer that opens with the same string joins the same
/// pool. In the integration-test rig, <c>SharedDbFixture</c> deliberately pins the app's Postgres
/// connection string to <c>Maximum Pool Size=5</c> to catch pool leaks in the app's runtime code;
/// if this one-shot startup fetch shared that pool, N parallel <c>InterfoldWebApplicationFactory</c>
/// builds would contend for those 5 slots plus whatever the app's own startup wanted, blowing the
/// 15s pool timeout with <c>NpgsqlException: connection pool has been exhausted</c>. Forcing
/// <c>Pooling=false</c> here gives the loader a dedicated physical connection per invocation,
/// isolated from the app pool's identity — the extra ~10-30ms of a full connect on each factory
/// build is trivial next to the alternative of failing every second startup. Production loads
/// secrets once at boot per process, so the &quot;no pool re-use&quot; cost is a rounding error there too.
/// </para>
/// </summary>
internal static class SecretsPreBuildLoader
{
    /// <summary>
    /// Every row the API-side <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{TOptions}"/>
    /// patchers may consult. Keep in sync with the key lists inside
    /// <see cref="AuthenticationSecretsPostConfigure"/>, <see cref="FirebaseClientSecretsPostConfigure"/>,
    /// and <see cref="FcmSecretsPostConfigure"/>.
    /// </summary>
    private static readonly SecretsStoreKey[] SnapshotKeys =
    [
        // Auth secrets consumed by AuthenticationSecretsPostConfigure
        SecretsStoreKeys.OAuthGoogleClientSecret,
        SecretsStoreKeys.OAuthDiscordClientSecret,
        SecretsStoreKeys.OAuthAppleClientSecret,
        SecretsStoreKeys.EncryptionPepper,
        SecretsStoreKeys.AuthDeepLinkSecret,
        SecretsStoreKeys.AuthJwtRsa256PrivatePem,
        SecretsStoreKeys.AuthJwtEs256PrivatePem,

        // Firebase client-init rows consumed by FirebaseClientSecretsPostConfigure
        SecretsStoreKeys.FirebaseClientAndroid,
        SecretsStoreKeys.FirebaseClientIos,
        SecretsStoreKeys.FirebaseClientWeb,

        // FCM v1 service-account credential consumed by FcmSecretsPostConfigure
        SecretsStoreKeys.FcmServiceAccountJson,
    ];

    public static SecretsSnapshot Load(IConfigurationBuilder cfg)
    {
        var config = (IConfigurationRoot)((IConfigurationBuilder)cfg).Build();
        var pgConn = config[OctoconEnvKeys.PostgresConnection];
        var snapshot = new SecretsSnapshot();

        if (!string.IsNullOrWhiteSpace(pgConn))
        {
            var rows = FetchFromPostgres(pgConn);
            snapshot.Populate(BuildSnapshotBuffer(rows));
            var leafPfxPassword = rows.TryGetValue(SecretsStoreKeys.CertsLeafPfxPassword.Value, out var pw) ? pw : null;
            ApplyLeafPfxPasswordIfNeeded(cfg, config, pgConnPresent: true, leafPfxPassword);
        }
        else
        {
            snapshot.Populate(BuildInMemorySeedBuffer(config));
            ApplyLeafPfxPasswordIfNeeded(cfg, config, pgConnPresent: false, leafPfxPassword: null);
        }

        return snapshot;
    }

    private static Dictionary<SecretsStoreKey, string?> BuildSnapshotBuffer(Dictionary<string, string?> rows)
    {
        var buffer = new Dictionary<SecretsStoreKey, string?>(SnapshotKeys.Length);
        foreach (var key in SnapshotKeys)
        {
            buffer[key] = rows.TryGetValue(key.Value, out var value) ? value : null;
        }
        return buffer;
    }

    /// <summary>
    /// No Postgres connection configured — pure InMemory persistence. Mirrors
    /// <c>InMemoryServiceCollectionExtensions.AddInMemoryPersistence</c>'s seeding contract by
    /// reading the same <c>OCTOCON_INMEMORY_SECRETS_SEED:*</c> keys one layer earlier. OAuth /
    /// Firebase client / FCM rows have no InMemory seed path and stay null, matching
    /// pre-existing behaviour.
    /// </summary>
    private static Dictionary<SecretsStoreKey, string?> BuildInMemorySeedBuffer(IConfigurationRoot config) => new()
    {
        [SecretsStoreKeys.EncryptionPepper] = config[OctoconEnvKeys.InMemorySecretsSeedEncryptionPepper],
        [SecretsStoreKeys.AuthJwtEs256PrivatePem] = config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtEs256PrivatePem],
        [SecretsStoreKeys.AuthDeepLinkSecret] = config[OctoconEnvKeys.InMemorySecretsSeedAuthDeepLinkSecret],
        [SecretsStoreKeys.AuthJwtRsa256PrivatePem] = config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtRsa256PrivatePem],
    };

    /// <summary>
    /// Single round trip for every snapshot row plus the leaf PFX password (12 keys total).
    /// Connection-level failures (Postgres unreachable at boot) surface as a fail-fast
    /// <see cref="InvalidOperationException"/> — equivalent to today's behaviour where
    /// <c>SecretsSnapshotLoader.StartingAsync</c> would throw and fail host startup the same way.
    /// </summary>
    private static Dictionary<string, string?> FetchFromPostgres(string pgConn)
    {
        var rows = new Dictionary<string, string?>(StringComparer.Ordinal);
        // Force Pooling=false so this one-shot fetch never competes with the app's runtime
        // pool. See the class-level "Non-pooled by design" note for the full rationale.
        var nonPooledConn = WithPoolingDisabled(pgConn);
        try
        {
            using var conn = new NpgsqlConnection(nonPooledConn);
            conn.Open();
            using var cmd = new NpgsqlCommand(
                "SELECT key, value FROM internal.secrets WHERE key = ANY(@keys)", conn);

            var keys = new string[SnapshotKeys.Length + 1];
            for (var i = 0; i < SnapshotKeys.Length; i++)
            {
                keys[i] = SnapshotKeys[i].Value;
            }
            keys[^1] = SecretsStoreKeys.CertsLeafPfxPassword.Value;
            cmd.Parameters.AddWithValue("keys", keys);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to fetch startup secrets from internal.secrets. Ensure Postgres is " +
                "reachable at startup and that DatabaseInitPhase has seeded the required rows.", ex);
        }

        return rows;
    }

    /// <summary>
    /// Returns a copy of <paramref name="pgConn"/> with <c>Pooling=false</c> forced on, regardless
    /// of what the input specified. Round-trips through <see cref="NpgsqlConnectionStringBuilder"/>
    /// so we never depend on brittle string manipulation and any operator-supplied keywords
    /// (host, port, credentials, timeouts, SSL settings, application_name, etc.) survive the
    /// round-trip untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why override rather than append.</b> Npgsql keys its pool on the full canonical
    /// connection string, so any pre-existing <c>Pooling=true</c> or a bare positive
    /// <c>Maximum Pool Size=N</c> in the input string would land us back in a shared pool with
    /// whoever else opens the same string. The whole point of this helper is to opt out of pool
    /// contention entirely, so we explicitly set the value rather than only appending when
    /// unset — that's the invariant the unit tests pin.
    /// </para>
    /// <para>
    /// Extracted (rather than inlined) so it can be exercised in isolation from
    /// <c>SecretsPreBuildLoaderPoolingTests</c> without spinning up Postgres.
    /// </para>
    /// </remarks>
    internal static string WithPoolingDisabled(string pgConn)
    {
        var builder = new NpgsqlConnectionStringBuilder(pgConn)
        {
            Pooling = false,
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Self-host only: triggered solely when the AppHost has injected a Kestrel default-cert
    /// path. Local dev (which uses the default dotnet dev cert) sees no path and this becomes
    /// a no-op regardless of persistence mode. Preserves the exact throw semantics of the
    /// retired <c>LoadLeafPfxPasswordFromStoreIfNeeded</c>: PFX path set + no Postgres
    /// connection → throw; PFX path set + row missing/empty → throw.
    /// </summary>
    private static void ApplyLeafPfxPasswordIfNeeded(
        IConfigurationBuilder cfg,
        IConfigurationRoot config,
        bool pgConnPresent,
        string? leafPfxPassword)
    {
        var pfxPath = config["Kestrel:Certificates:Default:Path"]
                      ?? Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Path");
        if (string.IsNullOrWhiteSpace(pfxPath)) return;

        // If the operator pinned a password via env (the legacy path) prefer that over the
        // store lookup. Lets local dev or one-off recovery flows bypass the DB roundtrip.
        var existingPassword = config["Kestrel:Certificates:Default:Password"];
        if (!string.IsNullOrWhiteSpace(existingPassword)) return;

        if (!pgConnPresent)
        {
            throw new InvalidOperationException(
                "Kestrel default-cert path is set but OCTOCON_POSTGRES_CONNECTION is missing; " +
                "cannot fetch certs:leaf_pfx_password from internal.secrets.");
        }

        if (string.IsNullOrEmpty(leafPfxPassword))
        {
            throw new InvalidOperationException(
                "Row internal.secrets[certs:leaf_pfx_password] is missing or empty; " +
                "re-run the bootstrapper so SecretsPhase + DatabaseInitPhase seed it.");
        }

        cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Certificates:Default:Password"] = leafPfxPassword,
        });
    }
}
