using System.Reflection;
using System.Security.Claims;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Microsoft.AspNetCore.Http;

namespace Interfold.Api.UnitTests.ModelBinding;

public sealed class SystemIdBindAsyncTests
{
    private static readonly ParameterInfo Parameter = typeof(SystemIdBindAsyncTests)
        .GetMethod(nameof(RouteParam), BindingFlags.NonPublic | BindingFlags.Static)!
        .GetParameters()[0];

    [Test]
    public async Task Bind_ExplicitSystemId_BindsSuccessfully()
    {
        var http = RouteContext("systemId", "sys1234");

        var bound = await SystemId.BindAsync(http, Parameter);

        await Assert.That(bound).IsEqualTo(new SystemId("sys1234"));
    }

    [Test]
    public async Task Bind_RegionPrefixedSystemId_StripsPrefixAndBindsSuccessfully()
    {
        var http = RouteContext("systemId", "nam:sys1234");

        var bound = await SystemId.BindAsync(http, Parameter);

        await Assert.That(bound).IsEqualTo(new SystemId("sys1234"));
    }

    [Test]
    public async Task Bind_Me_ResolvesPrincipalFromHttpContextItems()
    {
        var expected = new SystemId("caller77");
        var http = RouteContext("systemId", "me");
        http.Items[InterfoldHttpContextKeys.PrincipalId] = expected;

        var bound = await SystemId.BindAsync(http, Parameter);

        await Assert.That(bound).IsEqualTo(expected);
    }

    [Test]
    public async Task Bind_MeCaseInsensitive_ResolvesPrincipal()
    {
        var expected = new SystemId("caller77");
        var http = RouteContext("systemId", "ME");
        http.Items[InterfoldHttpContextKeys.PrincipalId] = expected;

        var bound = await SystemId.BindAsync(http, Parameter);

        await Assert.That(bound).IsEqualTo(expected);
    }

    [Test]
    public async Task Bind_Me_FallsBackToUserSubClaim_WhenItemsNotPresent()
    {
        var http = RouteContext("systemId", "me");
        var claims = new[] { new Claim(JwtClaimNames.Sub, "nam:jwt_user_id") };
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var bound = await SystemId.BindAsync(http, Parameter);

        await Assert.That(bound).IsEqualTo(new SystemId("jwt_user_id"));
    }

    [Test]
    public async Task Bind_Me_FailsWhenUnauthenticated()
    {
        var http = RouteContext("systemId", "me");

        var ex = await Assert.That(async () => await SystemId.BindAsync(http, Parameter))
            .Throws<InterfoldException>();

        await Assert.That(ex.Message).IsEqualTo("Cannot resolve 'me' for an unauthenticated request.");
    }

    private static HttpContext RouteContext(string name, string value)
    {
        var http = new DefaultHttpContext();
        http.Request.RouteValues[name] = value;
        return http;
    }

    private static void RouteParam(SystemId systemId) => _ = systemId;
}
