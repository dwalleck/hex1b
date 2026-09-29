using System.Runtime.CompilerServices;
using System.Text;
using System.Net.WebSockets;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Tokens;

namespace Hex1b.Tests.Diagnostics;

/// <summary>
/// A diagnostics-armed terminal records every write it makes to an observable presentation as an
/// accepted, refused or failed delivery record, separately from the model and application frames.
/// </summary>
[TestClass]
public class NativeDeliveryTests
{
    [TestMethod]
    public async Task Accepted_RecordsEachWriteWithItsLinks()
    {
        await using var harness = await ConsoleHarness.StartAsync(milestones: true);

        var enqueued = await harness.WriteAsync("ALPHA-OUTPUT");
        var result = harness.Capture(authorizations: [DiagnosticAuthorization.NativeOutput]);

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, result.Problem?.Message);
        Assert.AreEqual("console", result.DeliveryLayer);
        var record = result.Records.Single(r => Decode(r).Contains("ALPHA-OUTPUT", StringComparison.Ordinal));
        Assert.AreEqual(DiagnosticDeliveryOutcome.Accepted, record.Outcome);
        Assert.AreEqual(DiagnosticDeliverySource.WorkloadOutput, record.Source);
        Assert.AreEqual(record.Length, record.BytesAccepted, "an accepted write reported fewer bytes than it carried");
        Assert.IsGreaterThanOrEqualTo(record.StartTimestamp, record.EndTimestamp);
        // The workload's own enqueue counter is a different path from the pump's item.
        Assert.AreEqual(enqueued, record.OutputSequence, "the write carried another output item's sequence");
        Assert.AreNotEqual(record.ModelSequenceAtStart, record.OutputSequence, "fixture: the two sequences coincide, so a swap would pass");
        // The driver's own log is a different path from the recorder.
        StringAssert.Contains(harness.Driver.WrittenText, Decode(record));
        Assert.AreEqual(result.Records.Count, result.Totals!.Accepted + result.Totals.Refused + result.Totals.Failed,
            "totals do not count the returned records");
    }

    [TestMethod]
    public async Task Accepted_WithoutMilestonesReportsTheOutputItemUnavailable()
    {
        await using var harness = await ConsoleHarness.StartAsync(milestones: false);

        await harness.WriteAsync("BETA");
        var result = harness.Capture();

        Assert.IsTrue(result.Records.All(r => r.OutputSequence is null), "an untracked session reported output items");
        Assert.IsTrue(result.UnavailableFields.Any(f => f.Field == "records.outputSequence"), "the missing link was not explained");
    }

    [TestMethod]
    public async Task Phase_MatchesModelApplication()
    {
        foreach (var filtered in new[] { false, true })
        {
            await using var harness = await ConsoleHarness.StartAsync(filter: filtered);
            await harness.WriteAsync("SETTLE");
            var before = harness.ModelSequence();

            await harness.WriteAsync("PHASE-MARK");
            var after = harness.ModelSequence();
            var record = harness.Capture(authorizations: [DiagnosticAuthorization.NativeOutput]).Records
                .Single(r => Decode(r).Contains("PHASE-MARK", StringComparison.Ordinal));

            Assert.IsGreaterThan(before, after, "fixture: the marker did not advance the model");
            if (filtered)
            {
                Assert.AreEqual(DiagnosticDeliveryPhase.AfterModel, record.Phase);
                Assert.IsGreaterThanOrEqualTo(after, record.ModelSequenceAtStart, "an after-model write started before the model applied it");
            }
            else
            {
                Assert.AreEqual(DiagnosticDeliveryPhase.BeforeModel, record.Phase);
                Assert.AreEqual(before, record.ModelSequenceAtStart, "a raw passthrough write started after the model applied it");
            }
        }
    }

    [TestMethod]
    public async Task GatedRefusal_RecordsARefusedBatchThatNeverReachedTheHost()
    {
        await using var harness = await ConsoleHarness.StartAsync(milestones: true);

        // The host has left the geometry the batch was composed for.
        harness.Driver.TerminalSize = (41, 6);
        var refused = await harness.Workload.WriteRequiredIfGeometry("GATED-REFUSED", 40, 6)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        harness.Driver.TerminalSize = (40, 6);
        var applied = await harness.Workload.WriteRequiredIfGeometry("GATED-APPLIED", 40, 6)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var records = harness.Capture().Records.Where(r => r.Source == DiagnosticDeliverySource.GatedDelivery).ToList();

        Assert.AreEqual((NativeDeliveryOutcome.GeometryChanged, NativeDeliveryOutcome.Applied), (refused, applied), "fixture");
        Assert.HasCount(2, records);
        Assert.AreEqual((DiagnosticDeliveryOutcome.Refused, "geometry-changed", 0),
            (records[0].Outcome, records[0].Reason, records[0].BytesAccepted));
        Assert.AreEqual(DiagnosticDeliveryPhase.BeforeModel, records[0].Phase);
        Assert.IsNotNull(records[0].OutputSequence, "the refused batch lost its output item");
        Assert.IsGreaterThan(records[0].OutputSequence!.Value, records[1].OutputSequence ?? 0, "the applied batch did not carry the later output item");
        Assert.AreEqual(DiagnosticDeliveryOutcome.Accepted, records[1].Outcome);
        Assert.IsFalse(harness.Driver.WrittenText.Contains("GATED-REFUSED", StringComparison.Ordinal), "a refused batch reached the host");
        StringAssert.Contains(harness.Driver.WrittenText, "GATED-APPLIED");
    }

    [TestMethod]
    public async Task Failure_RecordedBeforeThePumpFails()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        await harness.WriteAsync("BEFORE");
        var error = new InvalidOperationException("write() failed with errno 5");
        harness.Driver.FailNextWrite(acceptedBytes: 3, error);

        harness.Workload.Write("FAILS-HERE");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Run.WaitAsync(TimeSpan.FromSeconds(5)));
        var result = harness.Capture();

        Assert.AreSame(error, failure.InnerException, "the write's own exception did not propagate unchanged");
        var record = result.Records.Single(r => r.Outcome == DiagnosticDeliveryOutcome.Failed);
        Assert.AreEqual($"{typeof(InvalidOperationException).FullName}: write() failed with errno 5", record.Error);
        Assert.AreEqual(3, record.BytesAccepted, "the bytes taken before the failure were not reported");
        Assert.AreEqual(1, result.Totals!.Failed);
    }

    [TestMethod]
    public async Task Failure_PreservesTheRunFailureOfAnUnarmedSession()
    {
        async Task<Exception> FailAsync(bool armed)
        {
            await using var harness = await ConsoleHarness.StartAsync(diagnostics: armed);
            await harness.WriteAsync("BEFORE");
            harness.Driver.FailNextWrite(0, new InvalidOperationException("write() failed with errno 5"));
            harness.Workload.Write("FAILS");
            return await Assert.ThrowsAsync<Exception>(() => harness.Run.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        var unarmed = await FailAsync(armed: false);
        var armed = await FailAsync(armed: true);

        Assert.AreEqual(
            (unarmed.GetType(), unarmed.Message, unarmed.InnerException?.GetType(), unarmed.InnerException?.Message),
            (armed.GetType(), armed.Message, armed.InnerException?.GetType(), armed.InnerException?.Message),
            "recording changed how a native write failure ends the run");
    }

    [TestMethod]
    public async Task TerminalControl_ExitSequencesAreRecordedWithoutOutputLinks()
    {
        var harness = await ConsoleHarness.StartAsync(milestones: true);

        await harness.DisposeAsync();
        // Read through the engine, as every consumer does: disposal must not hide its own writes.
        var result = harness.Capture(authorizations: [DiagnosticAuthorization.NativeOutput]);

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, result.Problem?.Message);
        var control = result.Records.Where(r => r.Source == DiagnosticDeliverySource.TerminalControl).ToList();
        Assert.IsNotEmpty(control, "the terminal's exit sequences were not recorded");
        Assert.IsTrue(control.All(r => r.Phase is null && r.OutputSequence is null), "a terminal-control write carried output links");
        StringAssert.Contains(Decode(control[^1]), "\u001b[?1049l");
        var fields = result.UnavailableFields.Select(f => f.Field).ToList();
        CollectionAssert.Contains(fields, "records.phase", "a terminal-control record's missing phase was not explained");
        CollectionAssert.Contains(fields, "records.outputSequence", "a terminal-control record's missing output item was not explained");
        Assert.AreEqual(fields.Count, fields.Distinct().Count(), "a field was explained twice: " + string.Join(", ", fields));
    }

    [TestMethod]
    public async Task Delivery_ZeroWritesIsAnEmptyObservation()
    {
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(new Hex1bAppWorkloadAdapter())
            .WithPresentation(new ConsolePresentationAdapter(new FakeConsoleDriver { TerminalSize = (40, 6) })).WithDimensions(40, 6).Build();
        var result = new TerminalDiagnostics(terminal, "delivery").CaptureDelivery(new DiagnosticDeliveryRequest());

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, "zero writes must be an observation, not unavailable");
        Assert.IsEmpty(result.Records);
        Assert.AreEqual((0L, 0L, 0L, 0L, (long?)null, (long?)null, 0L, 0),
            (result.Totals!.Accepted, result.Totals.Refused, result.Totals.Failed, result.Totals.BytesAccepted,
             result.Totals.FirstSequence, result.Totals.LastSequence, result.EvictedRecords, result.WritesInProgress));
        Assert.AreEqual("console", result.DeliveryLayer);
        Assert.IsNotNull(result.Identity?.Acquisition, "the observation has no acquisition interval");
    }

    [TestMethod]
    public async Task Delivery_SinceAndLimit()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        // Continue from the last returned record, never from totals.lastSequence.
        var baseline = harness.Capture().Records.LastOrDefault()?.Sequence ?? 0;

        for (var i = 0; i < 5; i++)
            await harness.WriteAsync($"ROW{i}");
        var all = harness.Capture(since: baseline);
        var page = harness.Capture(since: all.Records[1].Sequence, limit: 2);

        Assert.IsGreaterThanOrEqualTo(5, all.Records.Count);
        CollectionAssert.AreEqual(all.Records.Skip(2).Take(2).Select(r => r.Sequence).ToList(), page.Records.Select(r => r.Sequence).ToList());
        Assert.IsTrue(all.Records.Zip(all.Records.Skip(1)).All(p => p.Second.Sequence == p.First.Sequence + 1), "records are not oldest first and dense");
        foreach (var since in new[] { all.Records[^1].Sequence, 1_000_000L, long.MaxValue - 1, long.MaxValue })
            Assert.IsEmpty(harness.Capture(since: since).Records, $"since={since} returned records at or before it");
    }

    [TestMethod]
    public void Bounds_RingBytesAndTotals()
    {
        var recorder = new NativeDeliveryRecorder("console");
        var kib = new byte[1024];
        for (var i = 0; i < 10_000; i++)
            recorder.Complete(recorder.Begin(DiagnosticDeliverySource.WorkloadOutput, DiagnosticDeliveryPhase.BeforeModel, kib, null, 0),
                DiagnosticDeliveryOutcome.Accepted, kib.Length, null, null);
        var large = new byte[100 * 1024];
        recorder.Complete(recorder.Begin(DiagnosticDeliverySource.WorkloadOutput, DiagnosticDeliveryPhase.BeforeModel, large, null, 0),
            DiagnosticDeliveryOutcome.Accepted, large.Length, null, null);

        var snapshot = recorder.Read(0, NativeDeliveryRecorder.MaxRecords, includeBytes: true);
        var retained = snapshot.Records.Sum(r => r.Content is { } c ? Convert.FromBase64String(c).Length : 0);

        Assert.HasCount(4096, snapshot.Records);
        Assert.AreEqual(10_001 - 4096, snapshot.EvictedRecords);
        Assert.AreEqual(10_001, snapshot.Totals.Accepted);
        Assert.AreEqual(10_000L * 1024 + 100 * 1024, snapshot.Totals.BytesAccepted);
        Assert.IsLessThanOrEqualTo(NativeDeliveryRecorder.MaxRetainedBytes, retained, $"{retained} bytes retained");
        // Eviction frees only what the budget needs: at most one record's bytes below it.
        Assert.IsGreaterThanOrEqualTo(NativeDeliveryRecorder.MaxRetainedBytes - NativeDeliveryRecorder.MaxRecordBytes, retained, $"only {retained} bytes retained");
        Assert.AreEqual(retained, snapshot.Records.Where(r => !r.BytesEvicted).Sum(r => Math.Min(r.Length, NativeDeliveryRecorder.MaxRecordBytes)),
            "a record kept its bytes but was flagged evicted, or lost them unflagged");
        var last = snapshot.Records[^1];
        Assert.IsTrue(last.Truncated, "a 100 KiB write was not flagged truncated");
        Assert.AreEqual(NativeDeliveryRecorder.MaxRecordBytes, Convert.FromBase64String(last.Content!).Length);
        Assert.IsTrue(snapshot.Records[0].BytesEvicted, "the oldest record kept its bytes past the budget");
        Assert.AreEqual((10_001L - 4096 + 1, 10_001L), (snapshot.Records[0].Sequence, last.Sequence));
    }

    [TestMethod]
    public async Task NativeOutput_BytesOnlyWithAuthorization()
    {
        const string Sentinel = "ZQX-NATIVE-SECRET";
        await using var harness = await ConsoleHarness.StartAsync();
        await harness.WriteAsync(Sentinel);

        var plain = harness.Capture();
        var authorized = harness.Capture(authorizations: [DiagnosticAuthorization.NativeOutput]);
        var plainJson = JsonSerializer.Serialize(plain, DiagnosticsJsonContext.Default.DiagnosticDeliveryResult);

        Assert.IsFalse(plainJson.Contains(Sentinel, StringComparison.Ordinal), "sentinel in a default delivery result");
        Assert.IsTrue(plain.Records.All(r => r.Content is null), "bytes returned without authorization");
        Assert.AreEqual(DiagnosticCoverageState.Excluded, plain.ContentCoverage.Single(c => c.Content == DiagnosticContentClass.NativeOutput).State);
        Assert.IsTrue(authorized.Records.Any(r => Decode(r).Contains(Sentinel, StringComparison.Ordinal)), "native-output did not return the bytes");
        Assert.AreEqual(DiagnosticCoverageState.Included, authorized.ContentCoverage.Single(c => c.Content == DiagnosticContentClass.NativeOutput).State);
    }

    [TestMethod]
    public async Task Unavailable_NonNativeAndUnobservablePresentations()
    {
        var constructions = NativeDeliveryRecorder.ConstructionsForTesting.Value = new StrongBox<int>();
        await using (var headless = Hex1bTerminal.CreateBuilder().WithWorkload(new Hex1bAppWorkloadAdapter()).WithHeadless().WithDimensions(20, 3).Build())
        {
            var result = new TerminalDiagnostics(headless, "delivery").CaptureDelivery(new DiagnosticDeliveryRequest());
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-native-presentation"), (result.Outcome, result.Problem?.Code));
            Assert.IsEmpty(result.Records);
        }

        await using (var custom = Hex1bTerminal.CreateBuilder().WithWorkload(new Hex1bAppWorkloadAdapter())
                         .WithPresentation(new PlainPresentation()).WithDimensions(20, 3).Build())
        {
            var result = new TerminalDiagnostics(custom, "delivery").CaptureDelivery(new DiagnosticDeliveryRequest());
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "presentation-delivery-unobservable"), (result.Outcome, result.Problem?.Code));
        }

        Assert.AreEqual(0, constructions.Value, "a recorder was armed for a presentation that cannot report its writes");
    }

    [TestMethod]
    public async Task Unarmed_NoRecorderAndNoAddedAllocation()
    {
        var constructions = NativeDeliveryRecorder.ConstructionsForTesting.Value = new StrongBox<int>();
        await using (var unarmed = await ConsoleHarness.StartAsync(diagnostics: false))
        {
            for (var i = 0; i < 20; i++)
                await unarmed.WriteAsync($"UNARMED{i}");
            Assert.IsNull(unarmed.Terminal.NativeDelivery);
        }

        Assert.AreEqual(0, constructions.Value, $"{constructions.Value} recorders without a diagnostics engine");
        await using (await ConsoleHarness.StartAsync())
            Assert.IsGreaterThanOrEqualTo(1, constructions.Value, "fixture: the counter did not observe an armed session");

        // The unarmed helper makes exactly the presentation call, allocating nothing more.
        var presentation = new PlainPresentation();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(new Hex1bAppWorkloadAdapter())
            .WithPresentation(presentation).WithDimensions(20, 3).Build();
        var helper = typeof(Hex1bTerminal).GetMethod("WritePresentationAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .CreateDelegate<Func<IHex1bTerminalPresentationAdapter, ReadOnlyMemory<byte>, DiagnosticDeliverySource, DiagnosticDeliveryPhase?, long, CancellationToken, ValueTask>>(terminal);
        Func<IHex1bTerminalPresentationAdapter, ReadOnlyMemory<byte>, DiagnosticDeliverySource, DiagnosticDeliveryPhase?, long, CancellationToken, ValueTask> direct =
            (p, data, _, _, _, ct) => p.WriteOutputAsync(data, ct);
        var bytes = new byte[64];
        long Measure(Func<IHex1bTerminalPresentationAdapter, ReadOnlyMemory<byte>, DiagnosticDeliverySource, DiagnosticDeliveryPhase?, long, CancellationToken, ValueTask> write)
        {
            for (var i = 0; i < 100; i++)
                write(presentation, bytes, DiagnosticDeliverySource.WorkloadOutput, DiagnosticDeliveryPhase.BeforeModel, 1, default).GetAwaiter().GetResult();
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                write(presentation, bytes, DiagnosticDeliverySource.WorkloadOutput, DiagnosticDeliveryPhase.BeforeModel, 1, default).GetAwaiter().GetResult();
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }

        Assert.AreEqual(Measure(direct), Measure(helper), "the unarmed write path allocates more than the plain presentation call");
    }

    [TestMethod]
    public async Task Delivery_WritesNothingToThePresentation()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        await harness.WriteAsync("SETTLE");
        var before = harness.Driver.WriteCount;

        for (var i = 0; i < 20; i++)
            harness.Capture(authorizations: [DiagnosticAuthorization.NativeOutput]);

        Assert.AreEqual(before, harness.Driver.WriteCount, "a delivery capture wrote to the presentation");
        await harness.WriteAsync("POSITIVE-CONTROL");
        Assert.IsGreaterThan(before, harness.Driver.WriteCount, "fixture: output did not increment the write count");
    }

    [TestMethod]
    public async Task Sequences_DenseUnderConcurrentWriters()
    {
        var recorder = new NativeDeliveryRecorder("console");
        var data = new byte[16];
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                recorder.Complete(recorder.Begin(DiagnosticDeliverySource.WorkloadOutput, DiagnosticDeliveryPhase.BeforeModel, data, null, 0),
                    DiagnosticDeliveryOutcome.Accepted, data.Length, null, null);
        })));
        var snapshot = recorder.Read(0, NativeDeliveryRecorder.MaxRecords, includeBytes: false);
        CollectionAssert.AreEqual(Enumerable.Range(1, 4000).Select(i => (long)i).ToList(),
            snapshot.Records.Select(r => r.Sequence).ToList(), "sequences are not dense and unique");
        Assert.AreEqual(4000, snapshot.Totals.Accepted);

        // Workload output racing the terminal's own exit sequences: the terminal is disposed while
        // its pump is still running, not after the run was stopped.
        var raced = 0;
        for (var round = 0; round < 20; round++)
        {
            await using var harness = await ConsoleHarness.StartAsync();
            using var started = new ManualResetEventSlim();
            var written = 0;
            var writer = Task.Run(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    try { harness.Workload.Write($"R{round}-{i}\r\n"); }
                    catch (ObjectDisposedException) { return; }
                    Volatile.Write(ref written, i + 1);
                    if (i == 10)
                        started.Set();
                }
            });
            started.Wait(TestContext.Current.CancellationToken);
            var writtenAtDispose = Volatile.Read(ref written);
            await harness.Terminal.DisposeAsync();
            await writer;
            var read = harness.Capture();
            var sequences = read.Records.Select(r => r.Sequence).ToList();

            Assert.AreEqual(DiagnosticOutcome.Captured, read.Outcome, read.Problem?.Message);
            Assert.AreEqual(0, read.WritesInProgress, $"round {round}: a write was never completed");
            Assert.IsTrue(sequences.Zip(sequences.Skip(1)).All(p => p.Second == p.First + 1), $"round {round}: sequences not dense");
            Assert.AreEqual(read.Records.Count, read.Totals!.Accepted + read.Totals.Refused + read.Totals.Failed,
                $"round {round}: a record was completed twice or not at all");
            Assert.IsTrue(read.Records.Any(r => r.Source == DiagnosticDeliverySource.TerminalControl), $"round {round}: the exit sequences were not recorded");
            if (writtenAtDispose < 200)
                raced++;
        }

        // Positive control: disposal began while the writer was still writing, or the rounds proved nothing.
        Assert.IsGreaterThan(0, raced, "fixture: every writer finished before disposal began");
    }

    [TestMethod]
    public void Recorder_InProgressWriteHoldsBackLaterRecordsUntilItCompletes()
    {
        var recorder = new NativeDeliveryRecorder("console");
        var data = new byte[4];
        var slow = recorder.Begin(DiagnosticDeliverySource.WorkloadOutput, DiagnosticDeliveryPhase.BeforeModel, data, null, 0);
        recorder.Complete(recorder.Begin(DiagnosticDeliverySource.WorkloadOutput, DiagnosticDeliveryPhase.BeforeModel, data, null, 0),
            DiagnosticDeliveryOutcome.Accepted, data.Length, null, null);

        var during = recorder.Read(0, NativeDeliveryRecorder.MaxRecords, includeBytes: false);
        recorder.Complete(slow, DiagnosticDeliveryOutcome.Accepted, data.Length, null, null);
        var after = recorder.Read(during.Records.LastOrDefault()?.Sequence ?? 0, NativeDeliveryRecorder.MaxRecords, includeBytes: false);

        Assert.IsEmpty(during.Records, "a record after an in-progress write was returned, so a continuation would skip the slow one");
        Assert.AreEqual(1, during.WritesInProgress);
        Assert.AreEqual(2L, during.Totals.LastSequence, "fixture: totals count the completed later write");
        CollectionAssert.AreEqual(new[] { 1L, 2L }, after.Records.Select(r => r.Sequence).ToArray(), "continuing lost the slow write");
        Assert.AreEqual(0, after.WritesInProgress);
    }

    [TestMethod]
    public void Recorder_CompletesARecordOnlyOnce()
    {
        var recorder = new NativeDeliveryRecorder("console");
        var entry = recorder.Begin(DiagnosticDeliverySource.WorkloadOutput, DiagnosticDeliveryPhase.BeforeModel, new byte[8], null, 0);

        recorder.Complete(entry, DiagnosticDeliveryOutcome.Failed, 3, null, "first");
        recorder.Complete(entry, DiagnosticDeliveryOutcome.Accepted, 8, null, null);
        var snapshot = recorder.Read(0, NativeDeliveryRecorder.MaxRecords, includeBytes: false);

        Assert.AreEqual((DiagnosticDeliveryOutcome.Failed, 3, "first"),
            (snapshot.Records.Single().Outcome, snapshot.Records.Single().BytesAccepted, snapshot.Records.Single().Error));
        Assert.AreEqual((0L, 1L, 3L), (snapshot.Totals.Accepted, snapshot.Totals.Failed, snapshot.Totals.BytesAccepted),
            "a second completion was counted");
    }

    [TestMethod]
    public void Recorder_AWriteWithoutBytesIsNeverFlaggedEvicted()
    {
        var recorder = new NativeDeliveryRecorder("websocket");
        recorder.Complete(recorder.Begin(DiagnosticDeliverySource.WorkloadOutput, DiagnosticDeliveryPhase.BeforeModel, [], null, 0),
            DiagnosticDeliveryOutcome.Accepted, 0, null, null);
        var block = new byte[NativeDeliveryRecorder.MaxRecordBytes];
        for (var i = 0; i < 17; i++)
            recorder.Complete(recorder.Begin(DiagnosticDeliverySource.WorkloadOutput, DiagnosticDeliveryPhase.BeforeModel, block, null, 0),
                DiagnosticDeliveryOutcome.Accepted, block.Length, null, null);

        var records = recorder.Read(0, NativeDeliveryRecorder.MaxRecords, includeBytes: true).Records;

        Assert.IsFalse(records[0].BytesEvicted, "an empty write was flagged as having lost bytes it never had");
        Assert.IsTrue(records[1].BytesEvicted, "fixture: the budget did not evict the oldest real bytes");
    }

    [TestMethod]
    public async Task DisposedPresentation_UngatedIsRefusedAndGatedFailsWithNoBytes()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        await harness.WriteAsync("SETTLE");
        await harness.Presentation.DisposeAsync();

        harness.Workload.Write("AFTER-DISPOSE");
        var refused = await harness.WaitForRecordAsync(r => r.Outcome == DiagnosticDeliveryOutcome.Refused);
        var gated = harness.Workload.WriteRequiredIfGeometry("GATED-AFTER-DISPOSE", 40, 6);
        var failed = await harness.WaitForRecordAsync(r => r.Outcome == DiagnosticDeliveryOutcome.Failed);

        Assert.AreEqual(("presentation-disposed", 0), (refused.Reason, refused.BytesAccepted));
        Assert.AreEqual((DiagnosticDeliverySource.GatedDelivery, 0), (failed.Source, failed.BytesAccepted),
            "a gated write to a disposed presentation took no bytes, and the record must say so");
        StringAssert.Contains(failed.Error, nameof(ObjectDisposedException));
        await Assert.ThrowsAsync<Exception>(() => gated.WaitAsync(TimeSpan.FromSeconds(5)), "fixture: the gated write did not fail");
    }

    [TestMethod]
    public async Task InvalidRequests_AreRejectedBeforeReading()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        var cases = new (DiagnosticDeliveryRequest Request, string Code)[]
        {
            (new() { Limit = 0 }, "invalid-limit"),
            (new() { Limit = 4097 }, "invalid-limit"),
            (new() { Since = -1 }, "invalid-since"),
            (new() { Authorizations = [(DiagnosticAuthorization)99] }, "unsupported-authorization"),
        };
        foreach (var (request, code) in cases)
        {
            var result = harness.Diagnostics!.CaptureDelivery(request);
            Assert.AreEqual((DiagnosticOutcome.InvalidRequest, code), (result.Outcome, result.Problem?.Code), code);
            Assert.IsEmpty(result.Records);
        }
    }

    [TestMethod]
    public async Task Capabilities_ListTheDeliveryOperationAndLayer()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        var console = harness.Diagnostics!.GetCapabilities();
        await using var headless = Hex1bTerminal.CreateBuilder().WithWorkload(new Hex1bAppWorkloadAdapter()).WithHeadless().WithDimensions(20, 3).Build();
        var none = new TerminalDiagnostics(headless, "delivery").GetCapabilities();

        var operation = console.Operations.Single(o => o.Operation == TerminalDiagnostics.DeliveryOperation);
        Assert.AreEqual(DiagnosticLayer.NativeDelivery, operation.Layer);
        CollectionAssert.Contains(operation.Authorizations.ToList(), DiagnosticAuthorization.NativeOutput);
        Assert.IsTrue(console.Layers.Single(l => l.Layer == DiagnosticLayer.NativeDelivery).Available);
        var layer = none.Layers.Single(l => l.Layer == DiagnosticLayer.NativeDelivery);
        Assert.IsFalse(layer.Available);
        StringAssert.Contains(layer.Reason, "no native presentation");
    }

    [TestMethod]
    public async Task WebSocket_RefusedWhenNotOpenAndFailedWhenTheSendIsSwallowed()
    {
        var socket = new ControlledWebSocket();
        var presentation = new WebSocketPresentationAdapter(socket, 40, 6);
        var workload = new Hex1bAppWorkloadAdapter();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var diagnostics = new TerminalDiagnostics(terminal, "websocket");
        using var cts = new CancellationTokenSource();
        var run = terminal.RunAsync(cts.Token);

        async Task WriteAndSettleAsync(string text, int records)
        {
            workload.Write(text);
            for (var i = 0; i < 300 && (diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals?.LastSequence ?? 0) < records; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await WriteAndSettleAsync("OPEN", 1);
        socket.CurrentState = WebSocketState.Closed;
        await WriteAndSettleAsync("CLOSED", 2);
        socket.CurrentState = WebSocketState.Open;
        socket.FailSends = true;
        await WriteAndSettleAsync("BROKEN", 3);
        var result = diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest());

        Assert.AreEqual("websocket", result.DeliveryLayer);
        var outcomes = result.Records.Select(r => (r.Outcome, r.Reason)).ToList();
        CollectionAssert.AreEqual(new List<(DiagnosticDeliveryOutcome, string?)>
        {
            (DiagnosticDeliveryOutcome.Accepted, null),
            (DiagnosticDeliveryOutcome.Refused, "socket-not-open"),
            (DiagnosticDeliveryOutcome.Failed, null),
        }, outcomes, string.Join(", ", outcomes));
        StringAssert.Contains(result.Records[2].Error, nameof(WebSocketException));
        Assert.IsNull(result.Records[2].BytesAccepted, "a swallowed socket error cannot say how many bytes were sent");
        CollectionAssert.Contains(result.UnavailableFields.Select(f => f.Field).ToList(), "records.bytesAccepted",
            "the missing byte count was not explained");
        Assert.IsFalse(run.IsCompleted, "a swallowed socket error ended the run, which it never did before");
        Assert.HasCount(2, socket.Sent, "fixture: the open and failing writes both reached the socket; the closed one did not");
        await cts.CancelAsync();
        try { await run; } catch (Exception) { }
    }

    [TestMethod]
    public async Task WebSocket_ModelGatedDeliveryIsGatedAndAfterModel()
    {
        // Both routes the terminal takes for a batch it gates against its own model: raw, and filtered.
        foreach (var filtered in new[] { false, true })
        {
            var socket = new ControlledWebSocket();
            var presentation = new WebSocketPresentationAdapter(socket, 40, 6);
            var workload = new Hex1bAppWorkloadAdapter { GeometryGatedDeliveryEnforceable = true };
            var builder = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithPresentation(presentation).WithDimensions(40, 6);
            if (filtered)
                builder.AddPresentationFilter(new ObservingFilter());
            await using var terminal = builder.Build();
            var diagnostics = new TerminalDiagnostics(terminal, "websocket");
            using var cts = new CancellationTokenSource();
            var run = terminal.RunAsync(cts.Token);

            var outcome = await workload.WriteRequiredIfGeometry("MODEL-GATED", 40, 6).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var records = diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Records;

            Assert.AreEqual(NativeDeliveryOutcome.Applied, outcome, "fixture");
            Assert.AreEqual((DiagnosticDeliverySource.GatedDelivery, DiagnosticDeliveryPhase.AfterModel, DiagnosticDeliveryOutcome.Accepted),
                (records.Single().Source, records.Single().Phase, records.Single().Outcome),
                $"filtered={filtered}: a model-gated batch was labelled as ordinary output or by the wrong order");
            await cts.CancelAsync();
            try { await run; } catch (Exception) { }
        }
    }

    [TestMethod]
    public async Task Unarmed_ObservableWritePathsAllocateNothingExtra()
    {
        // The console adapter can report its writes, so the helper's own recorder check is what is measured.
        var driver = new FakeConsoleDriver { TerminalSize = (40, 6), DiscardWrites = true };
        var console = new ConsolePresentationAdapter(driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(25));
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(new Hex1bAppWorkloadAdapter())
            .WithPresentation(console).WithDimensions(40, 6).Build();
        Assert.IsNull(terminal.NativeDelivery, "fixture: no engine, so nothing is armed");
        var helper = typeof(Hex1bTerminal).GetMethod("WritePresentationAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .CreateDelegate<Func<IHex1bTerminalPresentationAdapter, ReadOnlyMemory<byte>, DiagnosticDeliverySource, DiagnosticDeliveryPhase?, long, CancellationToken, ValueTask>>(terminal);
        var bytes = new byte[64];
        Assert.AreEqual(
            Measure(() => console.WriteOutputAsync(bytes)),
            Measure(() => helper(console, bytes, DiagnosticDeliverySource.WorkloadOutput, DiagnosticDeliveryPhase.BeforeModel, 1, default)),
            "the unarmed helper allocates more than the console presentation's own write");

        // The WebSocket adapter's public write must be exactly its one write path, even when a send completes asynchronously.
        var socket = new ControlledWebSocket { YieldSends = true };
        await using var websocket = new WebSocketPresentationAdapter(socket, 40, 6);
        var core = typeof(WebSocketPresentationAdapter).GetMethod("WriteCoreAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .CreateDelegate<Func<ReadOnlyMemory<byte>, CancellationToken, StrongBox<NativeWriteResult>?, ValueTask>>(websocket);
        var text = Encoding.UTF8.GetBytes("websocket output");
        // Asynchronous completion leaves a few hundred bytes of thread-pool noise per thousand writes;
        // one extra boxed state machine is over a hundred bytes on every write.
        var coreBytes = Measure(() => core(text, default, null));
        var publicBytes = Measure(() => websocket.WriteOutputAsync(text));
        Assert.IsLessThan(8 * 1000, Math.Abs(publicBytes - coreBytes),
            $"the unarmed WebSocket write allocates {publicBytes} bytes per 1,000 writes against its one write path's {coreBytes}");

        static long Measure(Func<ValueTask> write)
        {
            static void Run(Func<ValueTask> write)
            {
                var pending = write();
                while (!pending.IsCompleted)
                    Thread.SpinWait(20);
                pending.GetAwaiter().GetResult();
            }

            for (var i = 0; i < 100; i++)
                Run(write);
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                Run(write);
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void PartialWrite_ReportsTheBytesTheKernelTook()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Linux pipe semantics.");
        var fds = new int[2];
        Assert.AreEqual(0, Pipe(fds), "fixture: pipe()");
        var (readFd, writeFd) = (fds[0], fds[1]);
        var driver = new UnixConsoleDriver(writeFd);
        var readerCount = 0;
        var unread = 0;
        var reader = new Thread(() =>
        {
            var buffer = new byte[4096];
            while (readerCount < 10_000)
            {
                var n = (int)Read(readFd, buffer, Math.Min(buffer.Length, 10_000 - readerCount));
                if (n <= 0)
                    break;
                readerCount += n;
            }

            // Let the writer fill the pipe and block, then count what the kernel holds unread.
            Thread.Sleep(300);
            _ = Ioctl(readFd, 0x541B /* FIONREAD */, out unread);
            Close(readFd);
        });
        reader.Start();

        var progress = new NativeWriteProgress();
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => driver.Write(new byte[1024 * 1024], progress));
        reader.Join();
        Close(writeFd);

        Assert.AreEqual("write() failed with errno 32", failure.Message, "the driver's failure changed");
        Assert.IsTrue(progress.Observed);
        Assert.AreEqual(readerCount + unread, progress.BytesAccepted,
            $"reader took {readerCount}, {unread} were left unread in the pipe, driver reported {progress.BytesAccepted}");
        Assert.IsGreaterThan(10_000, progress.BytesAccepted, "fixture: the write did not accept anything beyond what was read");
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "pipe", SetLastError = true)]
    private static extern int Pipe(int[] fds);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint Read(int fd, byte[] buffer, nint count);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, ulong request, out int value);

    private static string Decode(DiagnosticDeliveryRecord record) =>
        record.Content is { } content ? Encoding.UTF8.GetString(Convert.FromBase64String(content)) : "";

    // A diagnostics-armed terminal over the real console presentation and a test console driver.
    private sealed class ConsoleHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        private ConsoleHarness(FakeConsoleDriver driver, ConsolePresentationAdapter presentation, Hex1bAppWorkloadAdapter workload,
            Hex1bTerminal terminal, TerminalDiagnostics? diagnostics)
        {
            Driver = driver;
            Presentation = presentation;
            Workload = workload;
            Terminal = terminal;
            Diagnostics = diagnostics;
            Run = terminal.RunAsync(_cts.Token);
        }

        public FakeConsoleDriver Driver { get; }
        public ConsolePresentationAdapter Presentation { get; }
        public Hex1bAppWorkloadAdapter Workload { get; }
        public Hex1bTerminal Terminal { get; }
        public TerminalDiagnostics? Diagnostics { get; }
        public Task<int> Run { get; }

        public static async Task<ConsoleHarness> StartAsync(bool diagnostics = true, bool filter = false, bool milestones = false)
        {
            var driver = new FakeConsoleDriver { TerminalSize = (40, 6) };
            var presentation = new ConsolePresentationAdapter(driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(25));
            var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = milestones };
            var builder = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithPresentation(presentation).WithDimensions(40, 6);
            if (filter)
                builder.AddPresentationFilter(new PassFilter());
            var terminal = builder.Build();
            var harness = new ConsoleHarness(driver, presentation, workload, terminal,
                diagnostics ? new TerminalDiagnostics(terminal, "delivery") : null);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            return harness;
        }

        // Returns the output item the workload enqueued the text as (0 without milestones).
        public async Task<long> WriteAsync(string text)
        {
            Workload.Write(text);
            var enqueued = Workload.MilestoneOutputSequence;
            for (var i = 0; i < 500 && !Driver.WrittenText.Contains(text, StringComparison.Ordinal); i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.Contains(text, Driver.WrittenText, "fixture: the output never reached the driver");
            // The record completes right after the driver write returns.
            await Task.Delay(20, TestContext.Current.CancellationToken);
            return enqueued;
        }

        public long ModelSequence() => Terminal.CurrentModelSequence;

        public async Task<DiagnosticDeliveryRecord> WaitForRecordAsync(Func<DiagnosticDeliveryRecord, bool> match)
        {
            for (var i = 0; i < 500; i++)
            {
                if (Capture().Records.FirstOrDefault(match) is { } record)
                    return record;
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Fail("fixture: the expected record never appeared");
            return null!;
        }

        public DiagnosticDeliveryResult Capture(long? since = null, int? limit = null, IReadOnlyList<DiagnosticAuthorization>? authorizations = null) =>
            Diagnostics!.CaptureDelivery(new DiagnosticDeliveryRequest { Since = since, Limit = limit, Authorizations = authorizations });

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await _cts.CancelAsync();
            try { await Run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            await Terminal.DisposeAsync();
            await Presentation.DisposeAsync();
            _cts.Dispose();
        }
    }

    // An observer preserves the output, so a model-gated batch stays enforceable through it.
    private sealed class ObservingFilter : PassFilter, IHex1bTerminalOutputObserver
    {
    }

    private class PassFilter : IHex1bTerminalPresentationFilter
    {
        public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(IReadOnlyList<AppliedToken> appliedTokens, TimeSpan elapsed, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<AnsiToken>>(appliedTokens.Select(t => t.Token).ToArray());
        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    // A presentation that reports nothing about its writes.
    private sealed class PlainPresentation : IHex1bTerminalPresentationAdapter
    {
        public int Width => 20;
        public int Height => 3;
        public TerminalCapabilities Capabilities => TerminalCapabilities.Modern;
        public event Action<int, int>? Resized { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public async ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ReadOnlyMemory<byte>.Empty;
        }
        public (int Row, int Column) GetCursorPosition() => (0, 0);
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask EnterRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ExitRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
