using Interfold.Api.Services.Secrets;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Options;

namespace Interfold.Api.UnitTests.Options;

/// <summary>
/// Locks the contract of <see cref="FcmSecretsPostConfigure"/>: the optional
/// <c>fcm:service_account_json</c> row is copied verbatim onto
/// <see cref="FcmConfiguration.ServiceAccountJson"/>, a missing row leaves it null (the
/// <c>IFCMService</c> DI factory falls back to <c>NullFCMService</c>), and only the default
/// named options bucket is patched.
/// </summary>
public sealed class FcmSecretsPostConfigureTests
{
    private const string ServiceAccountJson = """{"type":"service_account","project_id":"test"}""";

    [Test]
    public async Task PostConfigure_RowPresent_PopulatesServiceAccountJson()
    {
        var snapshot = new StubSecretsSnapshot()
            .With(SecretsStoreKeys.FcmServiceAccountJson, ServiceAccountJson);
        var patcher = new FcmSecretsPostConfigure(snapshot);
        var options = new FcmConfiguration();

        patcher.PostConfigure(Microsoft.Extensions.Options.Options.DefaultName, options);

        await Assert.That(options.ServiceAccountJson).IsEqualTo(ServiceAccountJson)
            .Because("The fcm:service_account_json row must flow through verbatim — the Firebase Admin SDK, not this patcher, is responsible for parsing it.");
    }

    [Test]
    public async Task PostConfigure_MissingRow_LeavesNull()
    {
        var snapshot = new StubSecretsSnapshot(); // empty
        var patcher = new FcmSecretsPostConfigure(snapshot);
        var options = new FcmConfiguration();

        patcher.PostConfigure(Microsoft.Extensions.Options.Options.DefaultName, options);

        await Assert.That(options.ServiceAccountJson).IsNull()
            .Because("FCM is opt-in per deployment — an absent row must leave ServiceAccountJson null so the IFCMService DI factory falls back to NullFCMService instead of tripping a boot-time validator.");
    }

    [Test]
    public async Task PostConfigure_NamedInstance_LeavesUntouched()
    {
        var snapshot = new StubSecretsSnapshot()
            .With(SecretsStoreKeys.FcmServiceAccountJson, ServiceAccountJson);
        var patcher = new FcmSecretsPostConfigure(snapshot);
        var options = new FcmConfiguration();

        patcher.PostConfigure("other-name", options);

        await Assert.That(options.ServiceAccountJson).IsNull()
            .Because("PostConfigure guards on Options.DefaultName so a named bucket never receives the default's FCM credential.");
    }

    private sealed class StubSecretsSnapshot : ISecretsSnapshot
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public bool IsPopulated => true;

        public string? Get(SecretsStoreKey key) =>
            _values.TryGetValue(key.Value, out var v) ? v : null;

        public StubSecretsSnapshot With(SecretsStoreKey key, string value)
        {
            _values[key.Value] = value;
            return this;
        }
    }
}
