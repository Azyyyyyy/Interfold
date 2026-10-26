using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Auth.Contracts.Configuration;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Auth.Domain;

public class AuthHelper
{
    public static string CreateToken(AuthenticationConfiguration authConfig, DateTimeOffset expiresAt, DateTimeOffset now, Jti jti, SystemId systemId)
    {
        var headerJson = JsonSerializer.Serialize(
            new DeeplinkJwtHeader("ES256", "JWT"),
            AuthJwtJsonContext.Default.DeeplinkJwtHeader);

        var payloadJson = JsonSerializer.Serialize(
            new DeeplinkJwtPayload(
                authConfig.JwtAuthority,
                systemId.Value,
                now.ToUnixTimeSeconds(),
                now.ToUnixTimeSeconds(),
                expiresAt.ToUnixTimeSeconds(),
                jti.Value,
                "octocon:deeplink"),
            AuthJwtJsonContext.Default.DeeplinkJwtPayload);

        var encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{encodedHeader}.{encodedPayload}";

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(NormalizePem(authConfig.JwtEs256PrivateKeyPem).AsSpan());
        var signature = ecdsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var encodedSignature = Base64UrlEncode(signature);

        var token = $"{signingInput}.{encodedSignature}";
        return token;
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string NormalizePem(string pem)
        => pem.Replace("\\n", "\n", StringComparison.Ordinal);
}
