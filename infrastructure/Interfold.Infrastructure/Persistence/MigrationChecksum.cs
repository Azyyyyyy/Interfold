using System.Security.Cryptography;
using System.Text;

namespace Interfold.Infrastructure.Persistence;

internal static class MigrationChecksum
{
    // Ledger rows from Linux CI hash LF. Windows checkouts / docker contexts can
    // embed CRLF; strip CR so the same file matches either side.
    public static string Sha256Utf8(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
