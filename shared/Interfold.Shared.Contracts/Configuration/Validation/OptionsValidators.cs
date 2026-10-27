using Microsoft.Extensions.Options;

namespace Interfold.Shared.Contracts.Configuration.Validation;

[OptionsValidator]
public sealed partial class PersistenceConfigurationValidator : IValidateOptions<PersistenceConfiguration>;

[OptionsValidator]
public sealed partial class ObservabilityConfigurationValidator : IValidateOptions<ObservabilityConfiguration>;

[OptionsValidator]
public sealed partial class StorageConfigurationValidator : IValidateOptions<StorageConfiguration>;

[OptionsValidator]
public sealed partial class SocketConfigurationValidator : IValidateOptions<SocketConfiguration>;

[OptionsValidator]
public sealed partial class CorsOptionsValidator : IValidateOptions<CorsOptions>;
