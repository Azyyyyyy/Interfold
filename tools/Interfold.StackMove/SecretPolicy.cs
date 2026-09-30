namespace Interfold.StackMove;

internal static class SecretPolicy
{
    public static IReadOnlyList<SecretRow> Select(IEnumerable<SecretRow> secrets, StackKind target)
    {
        var selected = new List<SecretRow>();
        foreach (var secret in secrets)
        {
            if (IsConnectivity(secret.Key))
            {
                continue;
            }

            if (IsIdentity(secret.Key) || (target == StackKind.Sqlite && IsReplicatedWithPepper(secret.Key)))
            {
                selected.Add(secret);
            }
        }

        return selected;
    }

    public static bool IsConnectivity(string key) =>
        key.StartsWith("postgres:", StringComparison.Ordinal) ||
        key.StartsWith("scylla:", StringComparison.Ordinal);

    public static bool IsIdentity(string key) => key is
        "encryption:pepper" or
        "auth:jwt_rsa256_private_pem" or
        "auth:jwt_es256_private_pem" or
        "auth:deep_link_secret" or
        "certs:leaf_pfx_password";

    private static bool IsReplicatedWithPepper(string key) =>
        key.StartsWith("oauth:", StringComparison.Ordinal) ||
        key.StartsWith("firebase:", StringComparison.Ordinal) ||
        key.StartsWith("fcm:", StringComparison.Ordinal);
}
