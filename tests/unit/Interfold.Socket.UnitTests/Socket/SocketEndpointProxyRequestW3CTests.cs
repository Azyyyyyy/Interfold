using System.Diagnostics;
using System.Text.Json;
using Interfold.Socket.Api.Socket;
using Interfold.Socket.Contracts;

namespace Interfold.Api.UnitTests.Socket;

public sealed class SocketEndpointProxyRequestW3CTests
{
    [Test]
    public async Task Deserialize_WithoutW3CFields_LeavesThemNull()
    {
        const string json = """
            {"method":"GET","path":"/api/alters","body":""}
            """;

        var req = JsonSerializer.Deserialize<SocketEndpointProxyRequest>(json, SocketJson.Options);

        using (Assert.Multiple())
        {
            await Assert.That(req).IsNotNull();
            await Assert.That(req!.Method).IsEqualTo("GET");
            await Assert.That(req.Path).IsEqualTo("/api/alters");
            await Assert.That(req.Body).IsEqualTo("");
            await Assert.That(req.Traceparent).IsNull();
            await Assert.That(req.Tracestate).IsNull();
        }
    }

    [Test]
    public async Task Deserialize_WithW3CFields_PopulatesTraceparentAndTracestate()
    {
        const string json = """
            {
              "method": "GET",
              "path": "/api/alters",
              "body": "",
              "traceparent": "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
              "tracestate": "vendor=value"
            }
            """;

        var req = JsonSerializer.Deserialize<SocketEndpointProxyRequest>(json, SocketJson.Options);

        using (Assert.Multiple())
        {
            await Assert.That(req).IsNotNull();
            await Assert.That(req!.Traceparent)
                .IsEqualTo("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
            await Assert.That(req.Tracestate).IsEqualTo("vendor=value");
        }
    }

    [Test]
    public async Task TryActivateW3CContext_WithValidTraceparent_SetsActivityCurrent()
    {
        const string traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var prior = Activity.Current;

        using (var scope = WebSocketHandler.TryActivateW3CContext(traceparent, "vendor=value"))
        {
            await Assert.That(scope).IsNotNull();
            await Assert.That(Activity.Current).IsNotNull()
                .Because("Extract+Activate must install ambient context for the loopback hop");
            await Assert.That(Activity.Current!.OperationName).IsEqualTo("socket.endpoint.proxy");
            await Assert.That(Activity.Current.TraceId.ToHexString())
                .IsEqualTo("0af7651916cd43dd8448eb211c80319c");
            await Assert.That(Activity.Current.ParentSpanId.ToHexString())
                .IsEqualTo("b7ad6b7169203331");
        }

        await Assert.That(Activity.Current).IsEqualTo(prior)
            .Because("Dispose must restore the previous ambient context");
    }

    [Test]
    public async Task TryActivateW3CContext_WithoutTraceparent_ReturnsNull()
    {
        await Assert.That(WebSocketHandler.TryActivateW3CContext(null, null)).IsNull();
        await Assert.That(WebSocketHandler.TryActivateW3CContext("", "vendor=x")).IsNull();
        await Assert.That(WebSocketHandler.TryActivateW3CContext("   ", null)).IsNull();
    }
}
