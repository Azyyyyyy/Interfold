using Interfold.Api.Services.Secrets;
using Npgsql;

namespace Interfold.Api.UnitTests.Services;

/// <summary>
/// Pins the pool-isolation contract for <see cref="SecretsPreBuildLoader.WithPoolingDisabled"/>.
/// If any of these break, the loader will silently re-enter shared-pool territory and the
/// Npgsql pool-exhaustion regression that used to blow up parallel factory builds in the
/// integration suite (<c>NpgsqlException: connection pool has been exhausted, either raise
/// 'Max Pool Size' (currently 5) or 'Timeout' (currently 15 seconds)</c>) will come back.
///
/// Every test round-trips through the real <see cref="NpgsqlConnectionStringBuilder"/> so
/// keyword aliases and casing quirks are covered by the same parser Npgsql uses at
/// connection-open time.
/// </summary>
public sealed class SecretsPreBuildLoaderPoolingTests
{
    /// <summary>
    /// Core invariant: whatever the input said about pooling, the output must disable it.
    /// The pool identity is keyed on the canonical connection string, so a mismatched
    /// Pooling keyword is the difference between "join the app's 5-slot pool" and "open a
    /// dedicated physical connection".
    /// </summary>
    [Test]
    public async Task WithPoolingDisabled_WhenInputHasNoPoolingKeyword_ForcesPoolingFalse()
    {
        const string input = "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon";

        var output = SecretsPreBuildLoader.WithPoolingDisabled(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.Pooling).IsFalse();
    }

    /// <summary>
    /// Regression pin against a subtle failure mode: an operator (or test fixture) that
    /// left <c>Pooling=true</c> explicitly in the connection string previously would have
    /// been honoured, dropping us back into shared-pool contention. This test proves we
    /// override rather than append.
    /// </summary>
    [Test]
    public async Task WithPoolingDisabled_WhenInputExplicitlyEnablesPooling_OverridesToFalse()
    {
        const string input =
            "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon;Pooling=true";

        var output = SecretsPreBuildLoader.WithPoolingDisabled(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.Pooling).IsFalse();
    }

    /// <summary>
    /// The specific shape the integration-test fixture uses (<c>SharedDbFixture</c> pins
    /// <c>Maximum Pool Size=5</c>). If the loader ever re-joined that pool, N parallel
    /// factory builds would deadlock inside the 15s pool timeout with the exact error
    /// this Step-2 change eliminates. Pin that shape here explicitly so the regression
    /// is visible without having to re-run the whole integration suite.
    /// </summary>
    [Test]
    public async Task WithPoolingDisabled_WhenInputPinsMaxPoolSize_StillDisablesPooling()
    {
        const string input =
            "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon;Maximum Pool Size=5";

        var output = SecretsPreBuildLoader.WithPoolingDisabled(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.Pooling).IsFalse();
    }

    /// <summary>
    /// Round-trip invariant: overriding Pooling must not clobber any other keyword the
    /// operator supplied (credentials, TLS mode, application_name, timeouts, etc.). If
    /// this breaks, a production deployment could silently lose critical settings on
    /// startup — no thanks. Assert that the host/port/user/database quadruple survives.
    /// </summary>
    [Test]
    public async Task WithPoolingDisabled_PreservesAllOtherKeywords()
    {
        const string input =
            "Host=db.internal;Port=6543;Username=app;Password=super-secret;Database=octocon;" +
            "SSL Mode=Require;Application Name=api;Timeout=30;Command Timeout=60";

        var output = SecretsPreBuildLoader.WithPoolingDisabled(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        using (Assert.Multiple())
        {
            await Assert.That(parsed.Host).IsEqualTo("db.internal");
            await Assert.That(parsed.Port).IsEqualTo(6543);
            await Assert.That(parsed.Username).IsEqualTo("app");
            await Assert.That(parsed.Password).IsEqualTo("super-secret");
            await Assert.That(parsed.Database).IsEqualTo("octocon");
            await Assert.That(parsed.SslMode).IsEqualTo(SslMode.Require);
            await Assert.That(parsed.ApplicationName).IsEqualTo("api");
            await Assert.That(parsed.Timeout).IsEqualTo(30);
            await Assert.That(parsed.CommandTimeout).IsEqualTo(60);
            await Assert.That(parsed.Pooling).IsFalse();
        }
    }

    /// <summary>
    /// Idempotence: applying the transform twice must produce a string that still parses
    /// with <c>Pooling=false</c>. Guards against a future refactor that accidentally
    /// double-appends the keyword and produces a string Npgsql refuses to parse.
    /// </summary>
    [Test]
    public async Task WithPoolingDisabled_IsIdempotent()
    {
        const string input = "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon";

        var once  = SecretsPreBuildLoader.WithPoolingDisabled(input);
        var twice = SecretsPreBuildLoader.WithPoolingDisabled(once);

        var parsed = new NpgsqlConnectionStringBuilder(twice);
        await Assert.That(parsed.Pooling).IsFalse();
    }
}
