using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Infrastructure.Coordination;
using Interfold.Settings.Contracts.Configuration;
using Interfold.Settings.Domain.Abstractions.ImportJobs;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Infrastructure.DependencyInjection;

public static partial class ServiceCollectionExtensions
{
    /// <summary>Registers in-process cluster coordination services. Call after
    /// <see cref="AddInterfoldPersistence"/> in <c>Program.cs</c>.</summary>
    public static IServiceCollection AddInterfoldCluster(this IServiceCollection services)
    {
        services.AddSingleton<IClusterEventBus, InProcessEventBus>();

        // Per-system mutex lives in IImportOperationRepository (LWT on
        // active_import_by_system), so one channel + one worker is sufficient.
        services.AddSingleton<IImportJobQueue, InProcessImportJobQueue>();

        // Both implementations are registered concretely so the factory below can pick.
        services.AddOptions<FrontNotifierConfiguration>();
        services.AddOptions<TestHooksConfiguration>();

        services.AddSingleton<NullFCMService>();
        services.AddSingleton<FirebaseFCMService>();

        // Seeded fcm:service_account_json → real sender; otherwise no-op.
        services.AddSingleton<IFCMService>(sp =>
        {
            var serviceAccountJson = sp.GetRequiredService<IOptions<FcmConfiguration>>().Value.ServiceAccountJson;

            if (string.IsNullOrWhiteSpace(serviceAccountJson))
            {
                sp.GetRequiredService<ILogger<FirebaseFCMService>>().LogInformation(
                    "[fcm] internal.secrets:{Key} not seeded — using NullFCMService (push disabled).",
                    SecretsStoreKeys.FcmServiceAccountJson);
                return sp.GetRequiredService<NullFCMService>();
            }

            return sp.GetRequiredService<FirebaseFCMService>();
        });

        services.AddSingleton<InMemoryFcmSendOutbox>();
        services.AddSingleton<IFcmSendRecorder>(sp =>
        {
            var hooks = sp.GetService<IOptions<TestHooksConfiguration>>()?.Value;
            return hooks is { Enabled: true }
                ? sp.GetRequiredService<InMemoryFcmSendOutbox>()
                : NullFcmSendRecorder.Instance;
        });

        services.AddHostedService<FrontNotifierBackgroundService>();

        return services;
    }
}
