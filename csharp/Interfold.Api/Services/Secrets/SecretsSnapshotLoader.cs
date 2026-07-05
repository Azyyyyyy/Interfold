using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Interfold.Api.Services.Secrets;

/// <summary>
/// Loads the subset of <c>internal.secrets</c> rows that the API's
/// <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{TOptions}"/> patchers need
/// (<see cref="AuthenticationSecretsPostConfigure"/>,
/// <see cref="FirebaseClientSecretsPostConfigure"/>) into a synchronous
/// <see cref="SecretsSnapshot"/> once, before any hosted service's <c>StartAsync</c> fires.
///
/// <para>
/// This is the Slice 5 replacement for the pre-existing <c>SecretsBootstrapService</c>. The
/// lifecycle position is identical (<see cref="IHostedLifecycleService.StartingAsync"/>,
/// registered before any migration service in <c>Program.cs</c>), but the shape flips: this
/// class no longer mutates <c>IOptionsMonitor&lt;T&gt;.CurrentValue</c>. It only populates the
/// snapshot; the two post-configure implementations then read from that snapshot inside the
/// options-factory pipeline, and <c>.ValidateOnStart()</c> on <c>AuthenticationConfiguration</c>
/// enforces <see cref="System.ComponentModel.DataAnnotations.RequiredAttribute"/> on the four
/// mandatory secret fields.
/// </para>
///
/// <para>
/// Admin credentials (<c>postgres:admin_password</c>, <c>scylla:admin_*</c>) are read
/// directly by the migration services from <see cref="ISecretsStore"/>. The leaf PFX
/// password lives at <c>certs:leaf_pfx_password</c> but is fetched by a one-shot loader in
/// <c>Program.cs</c> before Kestrel binds — see that file for the Postgres-direct read path
/// that runs ahead of any hosted service. The FCM service-account JSON row
/// (<c>fcm:service_account_json</c>) is read by the <c>IFCMService</c> DI factory in
/// <c>ClusterServiceCollectionExtensions</c>. None of those three paths involve this loader.
/// </para>
/// </summary>
internal sealed class SecretsSnapshotLoader(
    ISecretsStore secretsStore,
    SecretsSnapshot snapshot,
    ILogger<SecretsSnapshotLoader> logger) : IHostedLifecycleService
{
    /// <summary>
    /// Every row the API-side <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{TOptions}"/>
    /// patchers may consult. Keep in sync with the key lists inside
    /// <see cref="AuthenticationSecretsPostConfigure"/> and
    /// <see cref="FirebaseClientSecretsPostConfigure"/> — a row read here that no patcher
    /// consumes is dead weight, and a patcher that consults a row absent from this list
    /// silently sees <c>null</c> forever.
    /// </summary>
    private static readonly SecretsStoreKey[] LoadedKeys =
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
    ];

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[secrets-snapshot] Loading {Count} row(s) from store...", LoadedKeys.Length);

        var buffer = new Dictionary<SecretsStoreKey, string?>(LoadedKeys.Length);
        var present = 0;
        foreach (var key in LoadedKeys)
        {
            var value = await secretsStore.GetAsync(key, cancellationToken).ConfigureAwait(false);
            buffer[key] = value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                present++;
            }
        }

        snapshot.Populate(buffer);

        logger.LogInformation(
            "[secrets-snapshot] Populated snapshot with {Present}/{Total} row(s) present.",
            present, LoadedKeys.Length);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
