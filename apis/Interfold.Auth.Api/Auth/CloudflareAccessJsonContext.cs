using System.Text.Json.Serialization;

namespace Interfold.Auth.Api.Auth;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CloudflareAccessIdentityJson))]
[JsonSerializable(typeof(CloudflareAccessIdpJson))]
[JsonSerializable(typeof(CloudflareAccessOidcClaimsJson))]
internal sealed partial class CloudflareAccessJsonContext : JsonSerializerContext;
