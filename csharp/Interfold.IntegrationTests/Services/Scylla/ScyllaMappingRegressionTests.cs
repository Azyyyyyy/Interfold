using Interfold.Contracts.Enums;
using Interfold.Infrastructure.Scylla.Repository;

namespace Interfold.IntegrationTests.Services.Scylla;

public sealed class ScyllaMappingRegressionTests : BaseEndpointTest
{
    [Test]
    public async Task PollUuidParsing_AcceptsCompactAndHyphenated_RejectsInvalid()
    {
        var compact = Guid.NewGuid().ToString("N");
        var hyphenated = Guid.NewGuid().ToString("D");

        var compactOk = ScyllaPollRepository.TryParseUuid(compact, out var compactParsed);
        var hyphenatedOk = ScyllaPollRepository.TryParseUuid(hyphenated, out var hyphenatedParsed);
        var invalidOk = ScyllaPollRepository.TryParseUuid("not-a-guid", out _);

        using (Assert.Multiple())
        {
            await Assert.That(compactOk).IsTrue();
            await Assert.That(hyphenatedOk).IsTrue();
            await Assert.That(invalidOk).IsFalse();
            await Assert.That(compactParsed.ToString("N")).IsEqualTo(compact);
            await Assert.That(hyphenatedParsed.ToString("D")).IsEqualTo(hyphenated);
        }
    }

    [Test]
    public async Task PollTypeMapping_HandlesKnownAndBoundaryValues()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ScyllaPollRepository.ToPollCode(PollType.Vote)).IsEqualTo((short)0);
            await Assert.That(ScyllaPollRepository.ToPollCode(PollType.Choice)).IsEqualTo((short)1);
            await Assert.That(ScyllaPollRepository.ToPollCode(PollType.Approval)).IsEqualTo((short)2);

            await Assert.That(ScyllaPollRepository.ToPollType(0)).IsEqualTo(PollType.Vote);
            await Assert.That(ScyllaPollRepository.ToPollType(1)).IsEqualTo(PollType.Choice);
            await Assert.That(ScyllaPollRepository.ToPollType(2)).IsEqualTo(PollType.Approval);
            await Assert.That(ScyllaPollRepository.ToPollType(short.MinValue)).IsEqualTo(PollType.Vote);
            await Assert.That(ScyllaPollRepository.ToPollType(short.MaxValue)).IsEqualTo(PollType.Vote);
        }
    }

    [Test]
    public async Task FriendshipLevelMapping_HandlesKnownAndBoundaryValues()
    {
        using (Assert.Multiple())
        {
            await Assert.That(FriendshipLevelExtensions.FromCode(0)).IsEqualTo(FriendshipLevel.Friend);
            await Assert.That(FriendshipLevelExtensions.FromCode(1)).IsEqualTo(FriendshipLevel.TrustedFriend);
            await Assert.That(FriendshipLevelExtensions.FromCode(short.MinValue)).IsEqualTo(FriendshipLevel.Friend);
            await Assert.That(FriendshipLevelExtensions.FromCode(short.MaxValue)).IsEqualTo(FriendshipLevel.Friend);
        }
    }

    [Test]
    public async Task UuidParsingRegression_TagJournalSettings_AcceptsCompactAndRejectsInvalid()
    {
        var tag = Guid.NewGuid().ToString("N");
        var journal = Guid.NewGuid().ToString("N");
        var field = Guid.NewGuid().ToString("N");

        using (Assert.Multiple())
        {
            await Assert.That(ScyllaTagRepository.TryParseUuid(tag, out _)).IsTrue();
            await Assert.That(ScyllaJournalRepository.TryParseUuid(journal, out _)).IsTrue();
            await Assert.That(ScyllaSettingsFieldRepository.TryParseUuid(field, out _)).IsTrue();

            await Assert.That(ScyllaTagRepository.TryParseUuid("bad", out _)).IsFalse();
            await Assert.That(ScyllaJournalRepository.TryParseUuid("bad", out _)).IsFalse();
            await Assert.That(ScyllaSettingsFieldRepository.TryParseUuid("bad", out _)).IsFalse();
        }
    }
}