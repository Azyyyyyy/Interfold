using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.AppHost;

/// <summary>Factory helpers for compose <see cref="Healthcheck"/> objects.</summary>
internal static class ComposeHealthcheck
{
    public static Healthcheck CmdShell(
        string command,
        string interval,
        string timeout,
        int retries,
        string startPeriod)
        => new()
        {
            Test = ["CMD-SHELL", command],
            Interval = interval,
            Timeout = timeout,
            Retries = retries,
            StartPeriod = startPeriod,
        };

    public static Healthcheck Cmd(
        string interval,
        string timeout,
        int retries,
        string startPeriod,
        params string[] command)
        => new()
        {
            Test = ["CMD", .. command],
            Interval = interval,
            Timeout = timeout,
            Retries = retries,
            StartPeriod = startPeriod,
        };
}

/// <summary>Pairs <c>WithVolume</c> with <see cref="ContainerLifetime.Persistent"/>. A
/// named volume without persistent lifetime silently re-creates on every
/// <c>docker compose up</c>, blowing away the data it was meant to preserve.</summary>
internal static class PersistentResourceExtensions
{
    public static IResourceBuilder<T> AsPersistent<T>(
        this IResourceBuilder<T> builder,
        string volumeName,
        string containerPath)
        where T : ContainerResource
    {
        return builder
            .WithVolume(volumeName, containerPath)
            .WithLifetime(ContainerLifetime.Persistent);
    }
}

/// <summary>Re-pull on every run-mode start so local tags match the registry a
/// self-host operator would get. Unprefixed names (<c>interfold-wasm:dev</c>) are
/// local-only — Always would <c>docker pull docker.io/library/…</c> and fail even
/// when the image exists. Publish leaves compose pull_policy alone
/// (<c>update-images</c> owns that). Do not use on <c>AddDockerfile</c> resources.</summary>
internal static class RegistryImageExtensions
{
    public static IResourceBuilder<T> PullAlwaysInRunMode<T>(this IResourceBuilder<T> builder)
        where T : ContainerResource
    {
        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
            return builder;
        return builder.WithImagePullPolicy(ImagePullPolicy.Always);
    }

    public static IResourceBuilder<T> PullAlwaysInRunMode<T>(this IResourceBuilder<T> builder, ImageRef image)
        where T : ContainerResource
        => image.HasRegistryHost ? builder.PullAlwaysInRunMode() : builder;
}

/// <summary>
/// <c>AddParameter(name, value)</c> uses that string as the runtime value and never
/// reads <c>Parameters:*</c> — user-secrets and appsettings lose to the literal.
/// </summary>
internal static class ConfiguredParameterExtensions
{
    public static IResourceBuilder<ParameterResource> AddConfiguredParameter(
        this IDistributedApplicationBuilder builder,
        string parametersKey,
        string fallback = "",
        bool secret = false,
        bool publishValueAsDefault = false)
    {
        var name = AppHostParameterKeys.ToParameterName(parametersKey);
        return builder.AddParameter(
            name,
            () => builder.Configuration[parametersKey] ?? fallback,
            publishValueAsDefault: publishValueAsDefault,
            secret: secret);
    }
}
