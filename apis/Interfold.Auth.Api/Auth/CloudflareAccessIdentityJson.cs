using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Auth.Api.Auth;

internal sealed class CloudflareAccessIdentityJson
{
    [JsonPropertyName("idp")]
    public CloudflareAccessIdpJson? Idp { get; set; }

    [JsonPropertyName("oidc_fields")]
    public CloudflareAccessOidcClaimsJson? OidcFields { get; set; }

    [JsonPropertyName("custom")]
    public CloudflareAccessOidcClaimsJson? Custom { get; set; }

    [JsonPropertyName("id")]
    [JsonConverter(typeof(JsonStringOrNumberConverter))]
    public string? Id { get; set; }
}

// Access JWT `idp` is a type string or `{id,type,name}`; get-identity always uses the object.
[JsonConverter(typeof(CloudflareAccessIdpJsonConverter))]
internal sealed class CloudflareAccessIdpJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class CloudflareAccessOidcClaimsJson
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(JsonStringOrNumberConverter))]
    public string? Id { get; set; }

    [JsonPropertyName("sub")]
    [JsonConverter(typeof(JsonStringOrNumberConverter))]
    public string? Sub { get; set; }
}

internal sealed class CloudflareAccessIdpJsonConverter : JsonConverter<CloudflareAccessIdpJson>
{
    public override CloudflareAccessIdpJson? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return new CloudflareAccessIdpJson { Type = value, Name = value };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Access idp must be a string or object.");

        string? id = null;
        string? type = null;
        string? name = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var property = reader.GetString();
            reader.Read();
            string? value = null;
            if (reader.TokenType == JsonTokenType.String)
                value = reader.GetString();
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                reader.Skip();

            switch (property)
            {
                case "id":
                    id = value;
                    break;
                case "type":
                    type = value;
                    break;
                case "name":
                    name = value;
                    break;
            }
        }

        return new CloudflareAccessIdpJson { Id = id, Type = type, Name = name };
    }

    public override void Write(Utf8JsonWriter writer, CloudflareAccessIdpJson value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Id is not null)
            writer.WriteString("id", value.Id);
        if (value.Type is not null)
            writer.WriteString("type", value.Type);
        if (value.Name is not null)
            writer.WriteString("name", value.Name);
        writer.WriteEndObject();
    }
}

internal sealed class JsonStringOrNumberConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.Null => null,
            _ => throw new JsonException("Expected a JSON string or number."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
