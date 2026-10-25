using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.InMemory;
using Interfold.Infrastructure.Sqlite;
using Interfold.Infrastructure.Sqlite.Repository;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Settings;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Api.UnitTests.ImportJobs;

// PK dispatch is the stub-importer twin of SP; re-asserting the shape here keeps a
// regression in either handler loud. Per-format extras (recovery-code contract, cross-format
// concurrency) stay inline; shared skeleton is ImportDispatchScenario.
public sealed class ImportPkCommandHandlerDispatchTests
{
    [Test]
    public async Task HandleAsync_FreshDispatch_ReturnsQueuedAndEnqueuesOnePkItem()
    {
        await using var scenario = ImportDispatchScenario.ForPk();

        var result = await scenario.DispatchAsync(token: "synthetic-pk-token");

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsTrue();
            await Assert.That(result.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Queued);
            await Assert.That(result.Result!.Format).IsEqualTo(ImportFormat.PluralKit)
                .Because("PK dispatches must surface the 'pk' format so the worker routes to the PK runner — wiring it to 'sp' would silently run SP code against a PK token.");
            await Assert.That(scenario.Capture.Enqueued).HasCount(1);
            await Assert.That(scenario.Capture.Enqueued[0].RawPayload).EqualTo("{\"token\":\"synthetic-pk-token\"}")
                .Because("PK has no recovery-code concept; carrying one through would be a contract leak from the SP path.");
        }
    }

    [Test]
    public async Task HandleAsync_ConcurrentPkDispatch_CollapsesOntoSameOperation()
    {
        await using var scenario = ImportDispatchScenario.ForPk();

        var first = await scenario.DispatchAsync("idem-1", token: "synthetic-pk-token");
        var second = await scenario.DispatchAsync("idem-2", token: "synthetic-pk-token");

        using (Assert.Multiple())
        {
            await Assert.That(second.Result!.OperationId).IsEqualTo(first.Result!.OperationId)
                .Because("Repeated PK dispatches for the same system must collapse onto the in-flight op — same guarantee the SP handler gives.");
            await Assert.That(second.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Running);
            await Assert.That(scenario.Capture.Enqueued).HasCount(1)
                .Because("Only one PK worker run per active claim — duplicate enqueues would re-introduce the bug class on the PK side once the real importer lands.");
        }
    }

    // LWT mutex partitions on (system, format) — SP and PK for the same system run
    // independently. Inline (not on the shared scenario) because it needs two handlers.
    [Test]
    public async Task HandleAsync_SpAndPk_SameSystem_BothClaimDistinctSlots()
    {
        var factory = InMemoryServiceCollectionExtensions.CreateIsolatedConnectionFactory();
        var repo = new SqliteImportOperationRepository(factory, TimeProvider.System);
        var queue = new InProcessImportJobQueue();
        var capture = new CapturingQueue(queue);
        var sp = new ImportSpCommandHandler(repo, capture);
        var pk = new ImportPkCommandHandler(repo, capture);

        var spResult = await sp.HandleAsync(
            ImportDispatchScenario.NewSpEnvelope(ImportDispatchScenario.PkSystemId, "idem-sp", "synthetic-sp-token"));
        var pkResult = await pk.HandleAsync(
            ImportDispatchScenario.NewPkEnvelope(ImportDispatchScenario.PkSystemId, "idem-pk", "synthetic-pk-token"));

        using (Assert.Multiple())
        {
            await Assert.That(spResult.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Queued);
            await Assert.That(pkResult.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Queued)
                .Because("A PK dispatch must not be blocked by an in-flight SP dispatch for the same system — the mutex is per (system, format).");
            await Assert.That(spResult.Result!.OperationId).IsNotEqualTo(pkResult.Result!.OperationId)
                .Because("Distinct formats must get distinct operation_ids; aliasing them would corrupt the audit trail.");
            await Assert.That(capture.Enqueued).HasCount(2)
                .Because("Both dispatches must produce their own worker run — one per format.");
        }

        await queue.DisposeAsync();
    }

    [Test]
    public async Task HandleAsync_EmptyToken_RejectsWithoutClaimingOrEnqueuing()
    {
        await using var scenario = ImportDispatchScenario.ForPk();

        var result = await scenario.DispatchAsync(token: string.Empty);

        await Assert.That(result.Accepted).IsFalse()
            .Because("A blank PK token must fail validation before any mutex claim is attempted.");
        await Assert.That(scenario.Capture.Enqueued).IsEmpty()
            .Because("Validation failure must never push an item onto the worker queue.");
    }
}
