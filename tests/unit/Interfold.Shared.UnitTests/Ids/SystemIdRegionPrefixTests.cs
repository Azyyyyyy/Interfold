using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Interfold.Shared.Api.Controllers.Base;
using Interfold.Shared.Api.Middleware;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Interfold.Api.UnitTests.Ids;

public sealed class SystemIdRegionPrefixTests
{
    private const string RawId = "sys-0123456789abcdef";
    private const string ScopedId = "nam:sys-0123456789abcdef";
    private const string OtherRegionScopedId = "eur:sys-0123456789abcdef";

    private sealed class DummyInterfoldController : InterfoldControllerBase;

    private static HttpContext CreateTestContext(string? sub)
    {
        var context = new DefaultHttpContext();
        if (sub is not null)
        {
            var claims = new[] { new Claim(JwtClaimNames.Sub, sub) };
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        }

        var actionDescriptor = new ControllerActionDescriptor
        {
            ControllerTypeInfo = typeof(DummyInterfoldController).GetTypeInfo()
        };
        var endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(actionDescriptor), "Test");
        context.SetEndpoint(endpoint);
        return context;
    }

    [Test]
    public async Task StripRegionPrefix_WithPrefix_ReturnsRawId()
    {
        var result = SystemId.StripRegionPrefix(ScopedId);
        await Assert.That(result).IsEqualTo(RawId);
    }

    [Test]
    public async Task StripRegionPrefix_WithoutPrefix_ReturnsOriginal()
    {
        var result = SystemId.StripRegionPrefix(RawId);
        await Assert.That(result).IsEqualTo(RawId);
    }

    [Test]
    public async Task StripRegionPrefix_NullOrWhitespace_ReturnsInput()
    {
        await Assert.That(SystemId.StripRegionPrefix(null!)).IsNull();
        await Assert.That(SystemId.StripRegionPrefix("")).IsEqualTo("");
        await Assert.That(SystemId.StripRegionPrefix("   ")).IsEqualTo("   ");
    }

    [Test]
    public async Task Parse_WithScopedId_StripsPrefix()
    {
        var parsed = SystemId.Parse(ScopedId, null);
        await Assert.That(parsed.Value).IsEqualTo(RawId);
    }

    [Test]
    public async Task Parse_WithRawId_PreservesRawId()
    {
        var parsed = SystemId.Parse(RawId, null);
        await Assert.That(parsed.Value).IsEqualTo(RawId);
    }

    [Test]
    public async Task TryParse_WithScopedId_ReturnsTrueAndStripsPrefix()
    {
        var success = SystemId.TryParse(ScopedId, null, out var result);
        await Assert.That(success).IsTrue();
        await Assert.That(result.Value).IsEqualTo(RawId);
    }

    [Test]
    public async Task TryParse_WithRawId_ReturnsTrueAndPreservesRawId()
    {
        var success = SystemId.TryParse(RawId, null, out var result);
        await Assert.That(success).IsTrue();
        await Assert.That(result.Value).IsEqualTo(RawId);
    }

    [Test]
    public async Task TryParse_Null_ReturnsFalse()
    {
        await Assert.That(SystemId.TryParse(null, null, out _)).IsFalse();
    }

    [Test]
    public async Task JsonDeserialization_WithScopedId_StripsPrefix()
    {
        var json = $"\"{ScopedId}\"";
        var deserialized = JsonSerializer.Deserialize<SystemId>(json);
        await Assert.That(deserialized.Value).IsEqualTo(RawId);
    }

    [Test]
    public async Task JsonDeserialization_WithRawId_PreservesRawId()
    {
        var json = $"\"{RawId}\"";
        var deserialized = JsonSerializer.Deserialize<SystemId>(json);
        await Assert.That(deserialized.Value).IsEqualTo(RawId);
    }

    [Test]
    public async Task RepresentsSameUserAs_ScopedAndRaw_AreEqual()
    {
        var scoped = new SystemId(ScopedId);
        var raw = new SystemId(RawId);

        await Assert.That(scoped.RepresentsSameUserAs(raw)).IsTrue();
        await Assert.That(raw.RepresentsSameUserAs(scoped)).IsTrue();
    }

    [Test]
    public async Task RepresentsSameUserAs_DifferentRegions_AreEqual()
    {
        var nam = new SystemId(ScopedId);
        var eur = new SystemId(OtherRegionScopedId);

        await Assert.That(nam.RepresentsSameUserAs(eur)).IsTrue();
    }

    [Test]
    public async Task Middleware_WithScopedSub_ResolvesRawSystemId()
    {
        var middleware = new InterfoldPrincipalMiddleware(next: _ => Task.CompletedTask);
        var context = CreateTestContext(ScopedId);

        await middleware.InvokeAsync(context);

        var principal = context.Items[InterfoldPrincipalMiddleware.PrincipalIdItemKey] as SystemId?;
        await Assert.That(principal.HasValue).IsTrue();
        await Assert.That(principal!.Value.Value).IsEqualTo(RawId);
    }

    [Test]
    public async Task Middleware_WithRawSub_ResolvesRawSystemId()
    {
        var middleware = new InterfoldPrincipalMiddleware(next: _ => Task.CompletedTask);
        var context = CreateTestContext(RawId);

        await middleware.InvokeAsync(context);

        var principal = context.Items[InterfoldPrincipalMiddleware.PrincipalIdItemKey] as SystemId?;
        await Assert.That(principal.HasValue).IsTrue();
        await Assert.That(principal!.Value.Value).IsEqualTo(RawId);
    }

    [Test]
    public async Task Middleware_WithoutSub_Returns401()
    {
        var middleware = new InterfoldPrincipalMiddleware(next: _ => Task.CompletedTask);
        var context = CreateTestContext(sub: null);

        await middleware.InvokeAsync(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status401Unauthorized);
        await Assert.That(context.Items.ContainsKey(InterfoldPrincipalMiddleware.PrincipalIdItemKey)).IsFalse();
    }
}
