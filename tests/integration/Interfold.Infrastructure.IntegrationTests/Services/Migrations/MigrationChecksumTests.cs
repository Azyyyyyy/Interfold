using Interfold.Infrastructure.Persistence;

namespace Interfold.Infrastructure.IntegrationTests.Services.Migrations;

public sealed class MigrationChecksumTests
{
    [Test]
    public async Task Sha256Utf8MatchesAcrossLfAndCrlf()
    {
        const string lf = "CREATE TABLE t (\n  id int\n);\n";
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        await Assert.That(MigrationChecksum.Sha256Utf8(crlf))
            .IsEqualTo(MigrationChecksum.Sha256Utf8(lf));
    }
}
