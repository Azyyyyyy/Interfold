using System.Globalization;
using System.Security.Claims;
using Interfold.Shared.Api.Middleware;
using Interfold.Shared.Api.ModelBinding;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Interfold.Api.UnitTests.ModelBinding;

public sealed class SystemIdModelBinderTests
{
    private const string FieldName = "systemId";

    [Test]
    public async Task Bind_ExplicitSystemId_BindsSuccessfully()
    {
        var ctx = BuildContext(FieldName, "sys1234");

        await new SystemIdModelBinder().BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsTrue();
        await Assert.That(ctx.Result.Model).IsEqualTo(new SystemId("sys1234"));
        await Assert.That(ctx.ModelState.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task Bind_RegionPrefixedSystemId_StripsPrefixAndBindsSuccessfully()
    {
        var ctx = BuildContext(FieldName, "nam:sys1234");

        await new SystemIdModelBinder().BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsTrue();
        await Assert.That(ctx.Result.Model).IsEqualTo(new SystemId("sys1234"));
        await Assert.That(ctx.ModelState.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task Bind_Me_ResolvesPrincipalFromHttpContextItems()
    {
        var expectedPrincipal = new SystemId("caller77");
        var ctx = BuildContext(FieldName, "me");
        ctx.HttpContext.Items[InterfoldPrincipalMiddleware.PrincipalIdItemKey] = expectedPrincipal;

        await new SystemIdModelBinder().BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsTrue();
        await Assert.That(ctx.Result.Model).IsEqualTo(expectedPrincipal);
        await Assert.That(ctx.ModelState.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task Bind_MeCaseInsensitive_ResolvesPrincipal()
    {
        var expectedPrincipal = new SystemId("caller77");
        var ctx = BuildContext(FieldName, "ME");
        ctx.HttpContext.Items[InterfoldPrincipalMiddleware.PrincipalIdItemKey] = expectedPrincipal;

        await new SystemIdModelBinder().BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsTrue();
        await Assert.That(ctx.Result.Model).IsEqualTo(expectedPrincipal);
        await Assert.That(ctx.ModelState.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task Bind_Me_FallsBackToUserSubClaim_WhenItemsNotPresent()
    {
        var ctx = BuildContext(FieldName, "me");
        var claims = new[] { new Claim(JwtClaimNames.Sub, "nam:jwt_user_id") };
        ctx.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        await new SystemIdModelBinder().BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsTrue();
        await Assert.That(ctx.Result.Model).IsEqualTo(new SystemId("jwt_user_id"));
        await Assert.That(ctx.ModelState.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task Bind_Me_FailsWhenUnauthenticated()
    {
        var ctx = BuildContext(FieldName, "me");

        await new SystemIdModelBinder().BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsFalse();
        await Assert.That(ctx.ModelState.ContainsKey(FieldName)).IsTrue();
        var error = ctx.ModelState[FieldName]!.Errors[0].ErrorMessage;
        await Assert.That(error).IsEqualTo("Cannot resolve 'me' for an unauthenticated request.");
    }

    [Test]
    public async Task Provider_ReturnsBinderForSystemId()
    {
        var provider = new SystemIdModelBinderProvider();
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(SystemId));
        var context = new TestModelBinderProviderContext(metadata);

        var binder = provider.GetBinder(context);

        await Assert.That(binder).IsNotNull();
        await Assert.That(binder).IsTypeOf<SystemIdModelBinder>();
    }

    [Test]
    public async Task Provider_ReturnsNullForOtherTypes()
    {
        var provider = new SystemIdModelBinderProvider();
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(string));
        var context = new TestModelBinderProviderContext(metadata);

        var binder = provider.GetBinder(context);

        await Assert.That(binder).IsNull();
    }

    private static DefaultModelBindingContext BuildContext(string fieldName, string? rawValue)
    {
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(SystemId));
        var httpContext = new DefaultHttpContext();

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var routeValues = new RouteValueDictionary();
        if (rawValue is not null)
        {
            routeValues[fieldName] = rawValue;
        }

        return new DefaultModelBindingContext
        {
            ActionContext = actionContext,
            ModelName = fieldName,
            FieldName = fieldName,
            ModelMetadata = metadata,
            ModelState = new ModelStateDictionary(),
            ValueProvider = new RouteValueProvider(BindingSource.Path, routeValues, CultureInfo.InvariantCulture),
        };
    }

    private sealed class TestModelBinderProviderContext : ModelBinderProviderContext
    {
        public TestModelBinderProviderContext(ModelMetadata metadata)
        {
            Metadata = metadata;
        }

        public override ModelMetadata Metadata { get; }
        public override IModelMetadataProvider MetadataProvider { get; } = new EmptyModelMetadataProvider();
        public override BindingInfo BindingInfo => new();
        public override IModelBinder CreateBinder(ModelMetadata metadata) => throw new NotImplementedException();
        public override IModelBinder CreateBinder(ModelMetadata metadata, BindingInfo bindingInfo) => throw new NotImplementedException();
    }
}
