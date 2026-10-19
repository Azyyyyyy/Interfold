using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.InMemory;
using Interfold.Infrastructure.Sqlite;
using Interfold.Infrastructure.Sqlite.Repository;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Settings;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Api.UnitTests.ImportJobs;

public sealed class ImportOctoconCommandHandlerDispatchTests
{
    [Test]
    public async Task HandleAsync_FreshDispatch_ReturnsQueuedAndEnqueuesOneOctoconItem()
    {
        await using var scenario = ImportDispatchScenario.ForOctocon();

        var result = await scenario.DispatchAsync(token: "{\"alters\":[]}");

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsTrue();
            await Assert.That(result.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Queued);
            await Assert.That(result.Result!.Format).IsEqualTo(ImportFormat.Octocon)
                .Because("Octocon dispatches must surface the 'octocon' format so the worker routes to the Octocon runner.");
            await Assert.That(scenario.Capture.Enqueued.Count).IsEqualTo(1);
            await Assert.That(scenario.Capture.Enqueued[0].RawPayload).IsEqualTo("{\"alters\":[]}")
                .Because("Octocon dispatches must store the JSON payload into RawPayload for the worker runner.");
        }
    }

    [Test]
    public async Task HandleAsync_ConcurrentOctoconDispatch_CollapsesOntoSameOperation()
    {
        await using var scenario = ImportDispatchScenario.ForOctocon();

        var first = await scenario.DispatchAsync("idem-1", token: "{\"alters\":[]}");
        var second = await scenario.DispatchAsync("idem-2", token: "{\"alters\":[]}");

        using (Assert.Multiple())
        {
            await Assert.That(second.Result!.OperationId).IsEqualTo(first.Result!.OperationId)
                .Because("Repeated Octocon dispatches for the same system must collapse onto the in-flight op.");
            await Assert.That(second.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Running);
            await Assert.That(scenario.Capture.Enqueued.Count).IsEqualTo(1)
                .Because("Only one Octocon worker run per active claim.");
        }
    }

    [Test]
    public async Task HandleAsync_SpAndOctocon_SameSystem_BothClaimDistinctSlots()
    {
        var factory = InMemoryServiceCollectionExtensions.CreateIsolatedConnectionFactory();
        var repo = new SqliteImportOperationRepository(factory, TimeProvider.System);
        var queue = new InProcessImportJobQueue();
        var capture = new CapturingQueue(queue);
        var sp = new ImportSpCommandHandler(repo, capture);
        var octocon = new ImportOctoconCommandHandler(repo, capture);

        var spResult = await sp.HandleAsync(
            ImportDispatchScenario.NewSpEnvelope(ImportDispatchScenario.OctoconSystemId, "idem-sp", "synthetic-sp-token"));
        var octoconResult = await octocon.HandleAsync(
            ImportDispatchScenario.NewOctoconEnvelope(ImportDispatchScenario.OctoconSystemId, "idem-octocon", "{\"alters\":[]}"));

        using (Assert.Multiple())
        {
            await Assert.That(spResult.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Queued);
            await Assert.That(octoconResult.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Queued)
                .Because("An Octocon dispatch must not be blocked by an in-flight SP dispatch for the same system — mutex is per (system, format).");
            await Assert.That(spResult.Result!.OperationId).IsNotEqualTo(octoconResult.Result!.OperationId)
                .Because("Distinct formats must get distinct operation_ids.");
            await Assert.That(capture.Enqueued.Count).IsEqualTo(2)
                .Because("Both dispatches must produce their own worker run — one per format.");
        }

        await queue.DisposeAsync();
    }

    [Test]
    public async Task HandleAsync_EmptyPayload_RejectsWithoutClaimingOrEnqueuing()
    {
        await using var scenario = ImportDispatchScenario.ForOctocon();

        var result = await scenario.DispatchAsync(token: "   ");

        await Assert.That(result.Accepted).IsFalse()
            .Because("A blank Octocon payload must fail validation before any mutex claim is attempted.");
        await Assert.That(scenario.Capture.Enqueued).IsEmpty()
            .Because("Validation failure must never push an item onto the worker queue.");
    }
}
