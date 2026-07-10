using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Interfold.Contracts.Ids;

namespace Interfold.Domain;

public class EncryptionKey
{
    /// <summary>
    /// Derive the per-system encryption key from the caller's recovery code, the system's
    /// KDF salt, and the deployment-wide pepper.
    ///
    /// <para>
    /// Round-2 strong-typing rescan (Finding #12) promoted three of the four inputs and the
    /// return value from raw <c>string</c> to their existing wrappers. Pre-Round-2 this one
    /// signature was the funnel through which every strongly-typed value in the encryption
    /// pipeline was stripped back to string, so the two handlers each shipped a
    /// <c>.Value.Value.Value</c> unwrap at the call site and any future caller was one
    /// autocomplete away from re-introducing the pattern. The pepper stays raw because no
    /// wrapper exists for cryptographic deployment secrets today (creating one would only
    /// force an unnecessary <c>.Value</c> at every use).
    /// </para>
    /// </summary>
    public static EncryptionKeyMaterial DeriveKey(string pepper, SystemId systemId, RecoveryCode recoveryCode, EncryptionSalt salt)
    {
        // Step 1: SHA256(pepper + user_id + recovery_code)
        var hashInput = Encoding.UTF8.GetBytes(pepper + systemId.Value + recoveryCode.Value);
        var sha256Hash = SHA256.HashData(hashInput);

        // Step 2: Argon2id(sha256_hash, salt, t_cost=12, m_cost=65536, parallelism=1, hash_len=32)
        var saltBytes = Convert.FromBase64String(salt.Value);
        using var argon2 = new Argon2id(sha256Hash);
        argon2.Salt = saltBytes;
        argon2.DegreeOfParallelism = 1;
        argon2.MemorySize = 65536; // KB
        argon2.Iterations = 12;

        var keyBytes = argon2.GetBytes(32);
        return new EncryptionKeyMaterial(Convert.ToBase64String(keyBytes));
    }

    /// <summary>
    /// Compute the 9-char base64 checksum of a derived encryption key. Persisted to
    /// <c>encryption_state.key_checksum</c> so <c>RecoverEncryption</c> can verify a
    /// recovery-code guess without ever touching the underlying key material.
    /// </summary>
    public static KeyChecksum DeriveChecksum(EncryptionKeyMaterial key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Value));
        return new KeyChecksum(Convert.ToBase64String(hash)[..9]);
    }
}
