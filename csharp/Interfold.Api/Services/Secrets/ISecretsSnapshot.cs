using Interfold.Contracts.Secrets;

namespace Interfold.Api.Services.Secrets;

/// <summary>
/// Synchronous read-only view over the subset of <see cref="ISecretsStore"/> rows that the
/// API's <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{TOptions}"/> patchers
/// need to consult during options resolution.
///
/// <para>
/// <c>IPostConfigureOptions&lt;T&gt;.PostConfigure</c> is a synchronous callback fired inside
/// the options factory pipeline. It cannot await <c>ISecretsStore.GetAsync</c> without either
/// blocking a threadpool thread or requiring every consumer to accept an async options view
/// — both are anti-patterns for the DI-time snapshot pattern. Slice 5 resolves the impedance
/// mismatch by loading all interesting rows into this snapshot once during host startup
/// (<see cref="SecretsSnapshotLoader"/>) and reading them synchronously here.
/// </para>
///
/// <para>
/// <c>IsPopulated</c> lets tests assert the loader ran before dereference. Consumers that
/// need to distinguish "loader hasn't run yet" from "row genuinely absent" can check the
/// flag; production consumers rely on the ordering guarantee from
/// <see cref="Microsoft.Extensions.Hosting.IHostedLifecycleService.StartingAsync"/> and just
/// call <see cref="Get"/> directly.
/// </para>
/// </summary>
public interface ISecretsSnapshot
{
    /// <summary>
    /// Returns the value for <paramref name="key"/>, or <c>null</c> if the row is absent /
    /// blank / the snapshot hasn't been populated yet. Never throws.
    /// </summary>
    string? Get(SecretsStoreKey key);

    /// <summary>
    /// <c>true</c> after <see cref="SecretsSnapshotLoader"/> has finished its
    /// <see cref="Microsoft.Extensions.Hosting.IHostedLifecycleService.StartingAsync"/>
    /// call, regardless of how many rows were actually present in the store.
    /// </summary>
    bool IsPopulated { get; }
}
