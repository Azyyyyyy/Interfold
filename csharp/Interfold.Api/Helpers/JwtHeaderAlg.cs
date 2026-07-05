using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Interfold.Api.Helpers;

/// <summary>
/// Shared JWT-header <c>alg</c> extraction for the two custom ES256 signature validators
/// (HTTP auth pipeline and the WebSocket join path) — previously duplicated JsonDocument
/// probing in both.
/// </summary>
internal static class JwtHeaderAlg
{
    /// <summary>The only signature algorithm Interfold accepts.</summary>
    public const string Es256 = "ES256";

    /// <summary>Parses the <c>alg</c> member from a decoded JWT header JSON blob.</summary>
    /// <exception cref="SecurityTokenInvalidSignatureException">Missing/empty alg.</exception>
    public static string Parse(string headerJson)
    {
        using var headerDoc = JsonDocument.Parse(headerJson);
        if (!headerDoc.RootElement.TryGetProperty("alg", out var algProp)
            || string.IsNullOrWhiteSpace(algProp.GetString()))
        {
            throw new SecurityTokenInvalidSignatureException("Missing JWT algorithm.");
        }

        return algProp.GetString()!;
    }
}
