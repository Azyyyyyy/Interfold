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
internal sealed partial class CloudflareApiJsonContext : JsonSerializerContext;
