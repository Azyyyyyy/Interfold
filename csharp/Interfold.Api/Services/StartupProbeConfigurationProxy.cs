using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Interfold.Api.Services;

/// <summary>
/// Non-owning proxy that forwards every <see cref="IConfigurationRoot"/> and
/// <see cref="IConfiguration"/> call to the wrapped instance without exposing
/// <see cref="IDisposable"/>. Used by Program.cs's startup probe
/// <see cref="System.IServiceProvider"/> so that disposing the probe container does NOT
/// dispose the shared <see cref="ConfigurationManager"/> owned by the
/// <see cref="Microsoft.AspNetCore.Builder.WebApplicationBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b>
/// <see cref="Microsoft.Extensions.DependencyInjection.ServiceProvider"/> captures every
/// resolved singleton that implements <see cref="IDisposable"/> and disposes it on
/// container disposal. Microsoft.Extensions.Hosting deliberately registers
/// <see cref="IConfiguration"/> as a factory singleton (see
/// <c>HostBuilder.PopulateServiceCollection</c>: <c>services.AddSingleton(_ => appConfiguration)</c>)
/// "so it disposes with the service provider". When Program.cs builds a throw-away probe
/// SP off <c>builder.Services</c> to snapshot startup-only options (Persistence / Cors /
/// Cluster) and then <c>Dispose</c>s it, that captured singleton — the same
/// <see cref="ConfigurationManager"/> that backs <c>builder.Configuration</c> — is disposed
/// too. In production nothing else touches configuration between probe-dispose and
/// <c>builder.Build()</c>, so the fault is silent; under
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> the
/// factory's <c>ConfigureAppConfiguration</c> callback tries to <c>Add</c> a source to the
/// disposed manager during <c>HostApplicationBuilder.HostBuilderAdapter.ApplyChanges</c>
/// and every integration test that calls <c>CreateClient()</c> fails with
/// <c>ObjectDisposedException: 'ConfigurationManager'</c>.
/// </para>
/// <para>
/// <b>How this fixes it.</b> The proxy implements <see cref="IConfigurationRoot"/> (and
/// through it <see cref="IConfiguration"/>) but explicitly does NOT implement
/// <see cref="IDisposable"/>, so DI's disposal-capture walker skips it. Registering the
/// proxy in a clone of <c>builder.Services</c> via <see cref="SwapInto"/> preserves every
/// options-binding path — <c>Configure&lt;IConfiguration&gt;</c>,
/// <c>ConfigurationChangeTokenSource&lt;T&gt;</c>, etc. — while breaking the ownership
/// chain that would otherwise dispose the shared configuration when the probe SP is torn
/// down.
/// </para>
/// <para>
/// <b>What this is NOT.</b> Not a general-purpose non-owning wrapper for downstream code
/// to use. It only exists to make the startup-probe SP disposal-safe; production paths
/// resolve <see cref="IConfiguration"/> through the real DI container which SHOULD own the
/// <see cref="ConfigurationManager"/>. Do not register the proxy in
/// <c>builder.Services</c> itself.
/// </para>
/// </remarks>
internal sealed class StartupProbeConfigurationProxy(IConfigurationRoot inner) : IConfigurationRoot
{
    public string? this[string key]
    {
        get => inner[key];
        set => inner[key] = value;
    }

    public IEnumerable<IConfigurationSection> GetChildren() => inner.GetChildren();

    public IChangeToken GetReloadToken() => inner.GetReloadToken();

    public IConfigurationSection GetSection(string key) => inner.GetSection(key);

    public void Reload() => inner.Reload();

    public IEnumerable<IConfigurationProvider> Providers => inner.Providers;

    /// <summary>
    /// Returns a clone of <paramref name="source"/> with every registration whose service
    /// type is <see cref="IConfiguration"/> or <see cref="IConfigurationRoot"/> replaced by
    /// a <see cref="StartupProbeConfigurationProxy"/> wrapping <paramref name="root"/>.
    /// Other descriptors are copied verbatim so the probe SP resolves the same graph the
    /// real host would, minus the disposal ownership over the shared configuration.
    /// </summary>
    /// <remarks>
    /// The proxy instance is shared across both slots (they map to the same
    /// <see cref="ConfigurationManager"/> under Microsoft.Extensions.Hosting anyway), so
    /// consumers that resolve either interface see the same live values. Callers own the
    /// returned <see cref="IServiceCollection"/> and are expected to wrap the
    /// <c>BuildServiceProvider</c> result in a <c>using</c> block; the shared
    /// <paramref name="root"/> is unaffected by that dispose.
    /// </remarks>
    public static IServiceCollection SwapInto(IServiceCollection source, IConfigurationRoot root)
    {
        var proxy = new StartupProbeConfigurationProxy(root);
        IServiceCollection clone = new ServiceCollection();

        foreach (var descriptor in source)
        {
            if (descriptor.ServiceType == typeof(IConfiguration))
            {
                clone.AddSingleton<IConfiguration>(proxy);
            }
            else if (descriptor.ServiceType == typeof(IConfigurationRoot))
            {
                clone.AddSingleton<IConfigurationRoot>(proxy);
            }
            else
            {
                clone.Add(descriptor);
            }
        }

        return clone;
    }
}
