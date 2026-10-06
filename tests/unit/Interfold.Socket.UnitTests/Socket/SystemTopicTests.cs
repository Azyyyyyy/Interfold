using Interfold.Shared.Contracts.Ids;
using Interfold.Socket.Api.Socket;

namespace Interfold.Api.UnitTests.Socket;

public sealed class SystemTopicTests
{
    private const string RawId = "sys-0123456789abcdef";
    private const string ScopedId = "nam:sys-0123456789abcdef";
    private const string OtherRegionScopedId = "eur:sys-0123456789abcdef";

    [Test]
    public async Task TryParse_RawTopic_ExtractsRawId()
    {
        var success = SystemTopic.TryParse($"system:{RawId}", out var topic);

        await Assert.That(success).IsTrue();
        await Assert.That(topic.Id.Value).IsEqualTo(RawId);
    }

    [Test]
    public async Task TryParse_ScopedTopic_StripsRegionPrefix()
    {
        var success = SystemTopic.TryParse($"system:{ScopedId}", out var topic);

        await Assert.That(success).IsTrue();
        await Assert.That(topic.Id.Value).IsEqualTo(RawId);
    }

    [Test]
    public async Task TryParse_NonSystemTopic_ReturnsFalse()
    {
        var success = SystemTopic.TryParse($"user:{RawId}", out _);

        await Assert.That(success).IsFalse();
    }

    [Test]
    public async Task IdMatches_RawAndScoped_ReturnsTrue()
    {
        await Assert.That(SystemTopic.IdMatches(RawId, ScopedId)).IsTrue();
        await Assert.That(SystemTopic.IdMatches(ScopedId, RawId)).IsTrue();
    }

    [Test]
    public async Task IdMatches_DifferentRegionsSameRawId_ReturnsTrue()
    {
        await Assert.That(SystemTopic.IdMatches(ScopedId, OtherRegionScopedId)).IsTrue();
    }

    [Test]
    public async Task IdMatches_DifferentRawIds_ReturnsFalse()
    {
        await Assert.That(SystemTopic.IdMatches(ScopedId, "nam:sys-different")).IsFalse();
    }
}
