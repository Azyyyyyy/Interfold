using System.Text.Json.Serialization;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>How <c>edge-nginx</c> routes public traffic to API vs web.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EdgeRoutingMode>))]
public enum EdgeRoutingMode
{
    /// <summary>Single hostname: <c>/api/</c> → API, <c>/</c> → web.</summary>
    [JsonStringEnumMemberName("path")]
    Path,

    /// <summary>Separate hostnames: <see cref="EdgeRoutingSection.ApiHost"/> → API,
    /// <see cref="EdgeRoutingSection.WebHost"/> → web.</summary>
    [JsonStringEnumMemberName("subdomain")]
    Subdomain,
}
