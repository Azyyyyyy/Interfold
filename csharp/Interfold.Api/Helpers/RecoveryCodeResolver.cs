using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Interfold.Contracts;
using Interfold.Contracts.Ids;

namespace Interfold.Api.Helpers;

public static class RecoveryCodeResolver
{
    /// <summary>
    /// Decrypt a compact JWE recovery-code ciphertext and return the plaintext wrapped in a
    /// <see cref="RecoveryCode"/> whose <see cref="RecoveryCode.ToString"/> redacts.
    ///
    /// <para>
    /// Step 6 of the strong-typing rescan promoted the <c>out</c> from <c>string</c> to the
    /// wrapper: pre-Step-6, any incidental log statement between this method returning and
    /// the controller's <c>new RecoveryCode(...)</c> wrap would emit the plaintext recovery
    /// code verbatim through the raw-string interpolation path. Wrapping inside the
    /// resolver means the plaintext exists as a bare <c>string</c> only inside the private
    /// <see cref="TryDecryptJwe"/> body (which has no logging or interpolation surface) and
    /// every external observation goes through the redacted <c>ToString</c>.
    /// </para>
    /// </summary>
    public static bool TryResolve(string candidate, string privateKeyPem, out RecoveryCode recoveryCode, out ErrorCode errorCode)
    {
        recoveryCode = default;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            errorCode = ErrorCodes.RecoveryCodeNotProvided;
            return false;
        }
        if (!LooksLikeCompactJwe(candidate))
        {
            errorCode = ErrorCodes.RecoveryCodeNotJwe;
            return false;
        }

        if (!TryLoadEncryptionPrivateKey(ref privateKeyPem)
            || !TryDecryptJwe(candidate, privateKeyPem, out var plaintext))
        {
            errorCode = ErrorCodes.DecryptionError;
            return false;
        }

        errorCode = default;
        // Wrap-at-decrypt-boundary: the plaintext local dies with this method frame; every
        // downstream reference goes through the redacting RecoveryCode wrapper.
        recoveryCode = new RecoveryCode(plaintext);
        return !string.IsNullOrWhiteSpace(plaintext);
    }

    public static bool LooksLikeCompactJwe(string token) => token.Count(ch => ch == '.') == 4;

    private static bool TryLoadEncryptionPrivateKey(ref string privateKeyPem)
    {
        privateKeyPem = privateKeyPem.Trim().Replace("\\n", "\n", StringComparison.Ordinal);
        return privateKeyPem.Contains("BEGIN", StringComparison.Ordinal);
    }

    private static bool TryDecryptJwe(string compactJwe, string privateKeyPem, out string plaintext)
    {
        plaintext = string.Empty;

        try
        {
            var parts = compactJwe.Split('.');
            if (parts.Length != 5)
                return false;

            var headerJson = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(parts[0]));
            using var header = JsonDocument.Parse(headerJson);
            var alg = header.RootElement.TryGetProperty("alg", out var algProp) ? algProp.GetString() : null;
            var enc = header.RootElement.TryGetProperty("enc", out var encProp) ? encProp.GetString() : null;

            if (!string.Equals(alg, "RSA-OAEP-256", StringComparison.Ordinal)
                || !string.Equals(enc, "A256GCM", StringComparison.Ordinal))
            {
                return false;
            }

            var encryptedKey = WebEncoders.Base64UrlDecode(parts[1]);
            var iv = WebEncoders.Base64UrlDecode(parts[2]);
            var ciphertext = WebEncoders.Base64UrlDecode(parts[3]);
            var tag = WebEncoders.Base64UrlDecode(parts[4]);
            var aad = Encoding.ASCII.GetBytes(parts[0]);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            var cek = rsa.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);

            var decrypted = new byte[ciphertext.Length];
            using var aes = new AesGcm(cek, tag.Length);
            aes.Decrypt(iv, ciphertext, tag, decrypted, aad);

            plaintext = Encoding.UTF8.GetString(decrypted);
            return !string.IsNullOrWhiteSpace(plaintext);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
