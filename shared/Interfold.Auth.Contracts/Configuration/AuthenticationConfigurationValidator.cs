using Microsoft.Extensions.Options;

namespace Interfold.Auth.Contracts.Configuration;

[OptionsValidator]
public sealed partial class AuthenticationConfigurationValidator : IValidateOptions<AuthenticationConfiguration>;
