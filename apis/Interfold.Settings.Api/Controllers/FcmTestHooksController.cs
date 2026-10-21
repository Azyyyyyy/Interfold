using Interfold.Infrastructure.Coordination;
using Interfold.Shared.Api.Controllers.Base;
using Interfold.Shared.Contracts.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Interfold.Settings.Api.Controllers;

/// <summary>Test-only FCM outbox. 404 unless <c>OCTOCON_TEST_HOOKS=1</c>.</summary>
[Route("api/internal/test/fcm-sends")]
public sealed class FcmTestHooksController(
    IFcmSendRecorder recorder,
    IOptions<TestHooksConfiguration> hooks) : InterfoldControllerBase
{
    [HttpGet]
    public IActionResult List()
    {
        if (!hooks.Value.Enabled)
            return NotFound();

        return Ok(new { data = recorder.Snapshot() });
    }
}
