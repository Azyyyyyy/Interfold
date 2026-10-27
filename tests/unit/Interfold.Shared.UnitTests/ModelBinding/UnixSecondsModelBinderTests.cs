using Interfold.Shared.Api.Http;
using Interfold.Shared.Api.Models;
using Microsoft.AspNetCore.Http;

namespace Interfold.Api.UnitTests.ModelBinding;

public sealed class UnixSecondsQueryTests
{
    private const string EndAnchorField = "end_anchor";
    private const string EndAnchorMessage = "Invalid end anchor. Please pass a valid Unix timestamp.";
    private const string EndAnchorCode = "invalid_end_anchor";
    private const string StartField = "start";
    private const string StartMessage = "Invalid start anchor. Please pass a valid Unix timestamp.";
    private const string BetweenCode = "invalid_anchor";

    [Test]
    public async Task Bind_ValidUnixSeconds_ProducesValue()
    {
        var http = QueryContext(EndAnchorField, "1700000000");

        var bound = UnixSecondsQuery.Bind(http, EndAnchorField, EndAnchorCode, EndAnchorMessage);

        await Assert.That(bound.IsError).IsFalse();
        await Assert.That(bound.Value).IsEqualTo(new UnixSeconds(1_700_000_000L));
    }

    [Test]
    public async Task Bind_UnparseableValue_EmitsAttributeMessageAndCode()
    {
        var http = QueryContext(EndAnchorField, "not-a-number");

        var bound = UnixSecondsQuery.Bind(http, EndAnchorField, EndAnchorCode, EndAnchorMessage);

        await Assert.That(bound.IsError).IsTrue();
        await Assert.That(bound.Error!.Error).IsEqualTo(EndAnchorMessage);
        await Assert.That(bound.Error.Code.Value).IsEqualTo(EndAnchorCode);
    }

    [Test]
    public async Task Bind_BetweenStartField_UsesInvalidAnchorCode()
    {
        var http = QueryContext(StartField, "abc");

        var bound = UnixSecondsQuery.Bind(http, StartField, BetweenCode, StartMessage);

        await Assert.That(bound.IsError).IsTrue();
        await Assert.That(bound.Error!.Error).IsEqualTo(StartMessage);
        await Assert.That(bound.Error.Code.Value).IsEqualTo(BetweenCode);
    }

    [Test]
    public async Task Bind_MissingValue_EmitsAttributeMessageAndCode()
    {
        var http = new DefaultHttpContext();

        var bound = UnixSecondsQuery.Bind(http, EndAnchorField, EndAnchorCode, EndAnchorMessage);

        await Assert.That(bound.IsError).IsTrue();
        await Assert.That(bound.Error!.Error).IsEqualTo(EndAnchorMessage);
        await Assert.That(bound.Error.Code.Value).IsEqualTo(EndAnchorCode);
    }

    private static HttpContext QueryContext(string name, string value)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString($"?{name}={value}");
        return http;
    }
}
