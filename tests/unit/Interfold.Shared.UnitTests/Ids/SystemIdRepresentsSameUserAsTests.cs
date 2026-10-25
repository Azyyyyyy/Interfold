using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

// Pins the SystemId.RepresentsSameUserAs(SystemId) primitive that backs every
// SystemId-typed controller self-request guard.
public sealed class SystemIdRepresentsSameUserAsTests
{
    private static readonly SystemId Principal = new SystemId("abcdefg");

    [Test]
    public async Task SystemId_MatchingId_IsSelf()
    {
        SystemId candidate = new("abcdefg");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsTrue()
            .Because("A candidate with the same raw id represents the same user.");
    }

    [Test]
    public async Task SystemId_DifferentId_IsNotSelf()
    {
        SystemId candidate = new("xyzzyxq");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("Candidates with a different raw id are different users.");
    }

    // Blank inputs must not blow up defensive callers (test fixtures, migration scripts).
    [Test]
    public async Task SystemId_BlankValue_ReturnsFalse()
    {
        SystemId candidate = new(string.Empty);

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("Blank candidates cannot represent any user — the primitive must return false rather than throw so the guard is safe on defensive inputs.");
    }
}
