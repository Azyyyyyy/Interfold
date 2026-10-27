using System.Security.Claims;
using Interfold.Shared.Api.Http;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Microsoft.AspNetCore.Http;

namespace Interfold.Api.UnitTests.ModelBinding;

public sealed class SystemIdBindAsyncTests
{
    [Test]
    public async Task Rewrite_ExplicitSystemId_LeavesValueUnchanged()
    {
        var http = RouteContext("systemId", "sys1234");

        SystemIdBinding.RewriteMe(http);

        await Assert.That(http.Request.RouteValues["systemId"]).IsEqualTo("sys1234");
        await Assert.That(SystemId.TryParse((string)http.Request.RouteValues["systemId"]!, null, out var parsed)).IsTrue();
        await Assert.That(parsed).IsEqualTo(new SystemId("sys1234"));
    }

    [Test]
    public async Task Rewrite_RegionPrefixedSystemId_LeavesValueForTryParse()
    {
        var http = RouteContext("systemId", "nam:sys1234");

        SystemIdBinding.RewriteMe(http);

        await Assert.That(http.Request.RouteValues["systemId"]).IsEqualTo("nam:sys1234");
        await Assert.That(SystemId.TryParse((string)http.Request.RouteValues["systemId"]!, null, out var parsed)).IsTrue();
        await Assert.That(parsed).IsEqualTo(new SystemId("sys1234"));
    }

    [Test]
    public async Task Rewrite_Me_ResolvesPrincipalFromHttpContextItems()
    {
        var expected = new SystemId("caller77");
        var http = RouteContext("systemId", "me");
        http.Items[InterfoldHttpContextKeys.PrincipalId] = expected;

        SystemIdBinding.RewriteMe(http);

        await Assert.That(http.Request.RouteValues["systemId"]).IsEqualTo(expected.Value);
    }

    [Test]
    public async Task Rewrite_MeCaseInsensitive_ResolvesPrincipal()
    {
        var expected = new SystemId("caller77");
        var http = RouteContext("systemId", "ME");
        http.Items[InterfoldHttpContextKeys.PrincipalId] = expected;

        SystemIdBinding.RewriteMe(http);

        await Assert.That(http.Request.RouteValues["systemId"]).IsEqualTo(expected.Value);
    }

    [Test]
    public async Task Rewrite_Me_FallsBackToUserSubClaim_WhenItemsNotPresent()
    {
        var http = RouteContext("systemId", "me");
        var claims = new[] { new Claim(JwtClaimNames.Sub, "nam:jwt_user_id") };
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        SystemIdBinding.RewriteMe(http);

        await Assert.That(http.Request.RouteValues["systemId"]).IsEqualTo("jwt_user_id");
    }

    [Test]
    public async Task Rewrite_Me_FailsWhenUnauthenticated()
    {
        var http = RouteContext("systemId", "me");

        var ex = await Assert.That(() => SystemIdBinding.RewriteMe(http))
            .Throws<InterfoldException>();

        await Assert.That(ex!.Message).IsEqualTo("Cannot resolve 'me' for an unauthenticated request.");
    }

    private static HttpContext RouteContext(string name, string value)
    {
        var http = new DefaultHttpContext();
        http.Request.RouteValues[name] = value;
        return http;
    }
}
