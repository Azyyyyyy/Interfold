using System.Globalization;
using Interfold.Shared.Api.ModelBinding;
using Interfold.Shared.Contracts.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Interfold.Api.UnitTests.ModelBinding;

public sealed class EnumWireModelBinderTests
{
    private const string FieldName = "format";

    [Test]
    public async Task Bind_WireValue_BindsSuccessfully()
    {
        var ctx = BuildContext<ImportFormat>(FieldName, "sp");

        var binder = new EnumWireModelBinder<ImportFormat>();
        await binder.BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsTrue();
        await Assert.That(ctx.Result.Model).IsEqualTo(ImportFormat.SimplyPlural);
        await Assert.That(ctx.ModelState.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task Bind_MemberName_BindsSuccessfully()
    {
        var ctx = BuildContext<ImportFormat>(FieldName, "SimplyPlural");

        var binder = new EnumWireModelBinder<ImportFormat>();
        await binder.BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsTrue();
        await Assert.That(ctx.Result.Model).IsEqualTo(ImportFormat.SimplyPlural);
        await Assert.That(ctx.ModelState.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task Bind_CaseInsensitive_BindsSuccessfully()
    {
        var ctx = BuildContext<ImportFormat>(FieldName, "OCTOCON");

        var binder = new EnumWireModelBinder<ImportFormat>();
        await binder.BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsTrue();
        await Assert.That(ctx.Result.Model).IsEqualTo(ImportFormat.Octocon);
        await Assert.That(ctx.ModelState.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task Bind_UnknownValue_FailsAndAddsModelError()
    {
        var ctx = BuildContext<ImportFormat>(FieldName, "invalid_format");

        var binder = new EnumWireModelBinder<ImportFormat>();
        await binder.BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsFalse();
        await Assert.That(ctx.ModelState.ContainsKey(FieldName)).IsTrue();
        await Assert.That(ctx.ModelState.ErrorCount).IsEqualTo(1);
    }

    [Test]
    public async Task Provider_ReturnsBinderForEnumAndNullableEnum()
    {
        var provider = new EnumWireModelBinderProvider();

        var enumMetadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(ImportFormat));
        var nullableMetadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(ImportFormat?));
        var nonEnumMetadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(string));

        await Assert.That(provider.GetBinder(new TestModelBinderProviderContext(enumMetadata))).IsNotNull();
        await Assert.That(provider.GetBinder(new TestModelBinderProviderContext(nullableMetadata))).IsNotNull();
        await Assert.That(provider.GetBinder(new TestModelBinderProviderContext(nonEnumMetadata))).IsNull();
    }

    private static DefaultModelBindingContext BuildContext<TEnum>(string fieldName, string? rawValue)
        where TEnum : struct, Enum
    {
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(TEnum));
        var httpContext = new DefaultHttpContext();

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var queryValues = new QueryCollection(rawValue is not null
            ? new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { [fieldName] = rawValue }
            : new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());

        return new DefaultModelBindingContext
        {
            ActionContext = actionContext,
            ModelName = fieldName,
            FieldName = fieldName,
            ModelMetadata = metadata,
            ModelState = new ModelStateDictionary(),
            ValueProvider = new QueryStringValueProvider(BindingSource.Query, queryValues, CultureInfo.InvariantCulture),
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
