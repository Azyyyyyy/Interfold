using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Bootstrapper.Util;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CloudflareApiResponse<List<CloudflareZoneResult>>))]
[JsonSerializable(typeof(CloudflareApiResponse<CloudflareTunnelResult>))]
[JsonSerializable(typeof(CloudflareApiResponse<List<CloudflareTunnelResult>>))]
[JsonSerializable(typeof(CloudflareApiResponse<string>))]
[JsonSerializable(typeof(CloudflareApiResponse<JsonElement>))]
[JsonSerializable(typeof(CloudflareApiResponse<CloudflareDnsRecordResult>))]
[JsonSerializable(typeof(CloudflareApiResponse<List<CloudflareDnsRecordResult>>))]
[JsonSerializable(typeof(CloudflareCreateTunnelRequest))]
[JsonSerializable(typeof(CloudflarePutIngressRequest))]
[JsonSerializable(typeof(CloudflareDnsRecordRequest))]
[JsonSerializable(typeof(CloudflareApiResponse<CloudflareAccessOrganizationResult>))]
[JsonSerializable(typeof(CloudflareApiResponse<CloudflareIdentityProviderResult>))]
[JsonSerializable(typeof(CloudflareApiResponse<List<CloudflareIdentityProviderResult>>))]
[JsonSerializable(typeof(CloudflareApiResponse<CloudflareAccessAppResult>))]
[JsonSerializable(typeof(CloudflareApiResponse<List<CloudflareAccessAppResult>>))]
[JsonSerializable(typeof(CloudflareApiResponse<CloudflareAccessPolicyResult>))]
[JsonSerializable(typeof(CloudflareApiResponse<List<CloudflareAccessPolicyResult>>))]
[JsonSerializable(typeof(CloudflareApiResponse<CloudflareServiceTokenResult>))]
[JsonSerializable(typeof(CloudflareApiResponse<List<CloudflareServiceTokenResult>>))]
[JsonSerializable(typeof(CloudflareGoogleIdpRequest))]
[JsonSerializable(typeof(CloudflareSelfHostedAppRequest))]
[JsonSerializable(typeof(CloudflareAccessPolicyRequest))]
[JsonSerializable(typeof(CloudflareServiceTokenRequest))]
internal sealed partial class CloudflareApiJsonContext : JsonSerializerContext;
