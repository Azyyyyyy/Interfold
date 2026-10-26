using System.Text.Json.Serialization;

namespace Interfold.Auth.Domain;

internal sealed record DeeplinkJwtHeader(string Alg, string Typ);

internal sealed record DeeplinkJwtPayload(
    string Iss,
    string Sub,
    long Iat,
    long Nbf,
    long Exp,
    string Jti,
    string Scope);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeeplinkJwtHeader))]
[JsonSerializable(typeof(DeeplinkJwtPayload))]
internal sealed partial class AuthJwtJsonContext : JsonSerializerContext;
