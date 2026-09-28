using System.Collections.Concurrent;
using Hex1b.Diagnostics;
using Hex1b.Tokens;

namespace Hex1b.Tests.Diagnostics;

/// <summary>
/// A capture describes one terminal-model observation: its content, geometry, cursor, history
/// coverage, and model sequence all come from the same model read, whatever happens to the live
/// model afterwards.
/// </summary>
[TestClass]
public class TerminalDiagnosticsCoherenceTests
{
    [TestMethod]
    public async Task ModelSequence_AdvancesPerEventAndHoldsWithoutEvents()
    {
        await using var terminal = CreateTerminal(40, 6);
        var diagnostics = new TerminalDiagnostics(terminal, "sequence");

        var initial = Sequence(diagnostics.Capture(new DiagnosticCaptureRequest()));
        terminal.ApplyTokens([]);
        var afterEmptyBatch = Sequence(diagnostics.Capture(new DiagnosticCaptureRequest()));
        Apply(terminal, "a");
        var afterOutput = Sequence(diagnostics.Capture(new DiagnosticCaptureRequest()));
        terminal.Resize(30, 5);
        var afterResize = Sequence(diagnostics.Capture(new DiagnosticCaptureRequest()));
        var idle = diagnostics.Capture(new DiagnosticCaptureRequest());

        Assert.IsTrue(afterEmptyBatch > initial, $"sequence did not advance after an output batch ({initial} -> {afterEmptyBatch})");
        Assert.IsTrue(afterOutput > afterEmptyBatch, $"sequence did not advance after output ({afterEmptyBatch} -> {afterOutput})");
        Assert.IsTrue(afterResize > afterOutput, $"sequence did not advance after resize ({afterOutput} -> {afterResize})");
        Assert.AreEqual(afterResize, Sequence(idle), "sequence changed without a model-input event");
        Assert.IsFalse(idle.UnavailableFields.Any(f => f.Field == "identity.modelSequence"),
            "modelSequence is still reported unavailable");
    }

    [TestMethod]
    public async Task SameSequence_SameContent_ResizeControl()
    {
        await using var terminal = CreateTerminal(40, 6);
        Apply(terminal, "K=1");
        var diagnostics = new TerminalDiagnostics(terminal, "same-sequence");

        var before = diagnostics.Capture(new DiagnosticCaptureRequest());
        terminal.Resize(30, 6);
        var after = diagnostics.Capture(new DiagnosticCaptureRequest());
        var again = diagnostics.Capture(new DiagnosticCaptureRequest());

        Assert.AreNotEqual(Sequence(before), Sequence(after),
            $"sequence unchanged across resize ({Sequence(before)} == {Sequence(after)}) but geometry " +
            $"{before.Geometry!.Columns}x{before.Geometry.Rows} != {after.Geometry!.Columns}x{after.Geometry.Rows}");
        Assert.AreEqual(Sequence(after), Sequence(again));
        Assert.AreEqual(Fingerprint(after), Fingerprint(again), "equal sequences described different model states");
    }

    [TestMethod]
    public async Task SameSequence_SameContent_ConcurrentOutputAndResize()
    {
        await using var terminal = CreateTerminal(40, 6, retention: 50, reflow: true);
        var diagnostics = new TerminalDiagnostics(terminal, "concurrent");
        using var writerDone = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var k = 0; k < 300; k++)
            {
                Apply(terminal, $"\r\nK={k}");
                if (k % 5 == 0)
                    terminal.Resize(k % 10 == 0 ? 28 : 40, k % 10 == 0 ? 5 : 6);
                Thread.SpinWait(200);
            }
        });

        var captures = new List<DiagnosticCaptureResult>();
        while (!writer.IsCompleted || captures.Count < 400)
            captures.Add(diagnostics.Capture(new DiagnosticCaptureRequest { HistoryRows = 10 }));
        await writer;

        var conflicts = captures
            .GroupBy(Sequence)
            .Where(group => group.Select(Fingerprint).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToList();
        Assert.AreEqual(0, conflicts.Count,
            $"{conflicts.Count} model sequences described more than one state, first at {conflicts.FirstOrDefault()}");
        for (var i = 1; i < captures.Count; i++)
            Assert.IsTrue(Sequence(captures[i]) >= Sequence(captures[i - 1]), $"sequence went backwards at capture {i}");
        Assert.IsTrue(captures.Select(Sequence).Distinct().Count() > 10, "the reader never observed concurrent progress");
    }

    [TestMethod]
    [DataRow(DiagnosticCaptureFormat.Text)]
    [DataRow(DiagnosticCaptureFormat.Ansi)]
    public async Task MutationAfterModelRead_MatchesUnmutatedTwin(DiagnosticCaptureFormat format)
    {
        const string input = "\x1b[1;31mRED\x1b[0m before\r\nsecond row";
        await using var subject = CreateTerminal(40, 6, retention: 100, reflow: true);
        await using var twin = CreateTerminal(40, 6, retention: 100, reflow: true);
        Apply(subject, input);
        Apply(twin, input);
        var diagnostics = new TerminalDiagnostics(subject, "subject")
        {
            AfterModelReadForTesting = () =>
            {
                Apply(subject, "\x1b[2J\x1b[HZZZ overwritten");
                subject.Resize(25, 4);
            }
        };

        var observed = diagnostics.Capture(new DiagnosticCaptureRequest { Format = format });
        var expected = new TerminalDiagnostics(twin, "twin").Capture(new DiagnosticCaptureRequest { Format = format });

        Assert.AreEqual($"{expected.Geometry!.Columns}x{expected.Geometry.Rows}",
            $"{observed.Geometry!.Columns}x{observed.Geometry.Rows}", "geometry is not from the model read");
        Assert.AreEqual((expected.Geometry.CursorColumn, expected.Geometry.CursorRow),
            (observed.Geometry.CursorColumn, observed.Geometry.CursorRow), "cursor is not from the model read");
        Assert.AreEqual(expected.Content, observed.Content, "content changed after the model read");
        Assert.AreEqual((25, 4), (subject.Width, subject.Height), "fixture did not mutate the live model");
    }

    [TestMethod]
    public async Task MutationAfterModelRead_HistoryCoverageMatchesTwin()
    {
        var lines = string.Concat(Enumerable.Range(1, 30).Select(i => $"line-{i:00}\r\n"));
        await using var subject = CreateTerminal(40, 6, retention: 100);
        await using var twin = CreateTerminal(40, 6, retention: 100);
        Apply(subject, lines);
        Apply(twin, lines);
        var diagnostics = new TerminalDiagnostics(subject, "subject")
        {
            AfterModelReadForTesting = () => Apply(subject, string.Concat(Enumerable.Range(1, 10).Select(i => $"late-{i}\r\n")))
        };

        var observed = diagnostics.Capture(new DiagnosticCaptureRequest { HistoryRows = 50 });
        var expected = new TerminalDiagnostics(twin, "twin").Capture(new DiagnosticCaptureRequest { HistoryRows = 50 });

        Assert.AreEqual(expected.History!.AvailableRows, observed.History!.AvailableRows,
            $"availableRows {observed.History.AvailableRows} != twin {expected.History.AvailableRows}");
        Assert.AreEqual(expected.History.ReturnedRows, observed.History.ReturnedRows);
        Assert.AreEqual(expected.Content, observed.Content, "returned history is not from the model read");
    }

    [TestMethod]
    public async Task MutationAfterModelRead_EvictionAtCapacityMatchesTwin()
    {
        var lines = string.Concat(Enumerable.Range(1, 40).Select(i => $"line-{i:00}\r\n"));
        await using var subject = CreateTerminal(40, 6, retention: 20);
        await using var twin = CreateTerminal(40, 6, retention: 20);
        Apply(subject, lines);
        Apply(twin, lines);
        var diagnostics = new TerminalDiagnostics(subject, "subject")
        {
            AfterModelReadForTesting = () => Apply(subject, string.Concat(Enumerable.Range(1, 25).Select(i => $"evict-{i}\r\n")))
        };

        var observed = diagnostics.Capture(new DiagnosticCaptureRequest { HistoryRows = 20 });
        var expected = new TerminalDiagnostics(twin, "twin").Capture(new DiagnosticCaptureRequest { HistoryRows = 20 });

        Assert.AreEqual(expected.Content, observed.Content, "history rows evicted after the model read changed the result");
        Assert.AreEqual(expected.History!.ReturnedRows, observed.History!.ReturnedRows);
    }

    [TestMethod]
    public async Task SynchronizedUpdate_PendingIsDisclosedWithoutWaiting()
    {
        await using var terminal = CreateTerminal(40, 6);
        var diagnostics = new TerminalDiagnostics(terminal, "sync");
        var before = diagnostics.Capture(new DiagnosticCaptureRequest());
        Assert.IsFalse(before.SynchronizedUpdate!.Active, "no synchronized update has begun");

        Apply(terminal, "\x1b[?2026hPART");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var during = diagnostics.Capture(new DiagnosticCaptureRequest());
        started.Stop();

        Assert.IsTrue(started.Elapsed < TimeSpan.FromMilliseconds(500),
            $"capture waited {started.Elapsed.TotalMilliseconds:0} ms for the pending update");
        Assert.IsTrue(during.SynchronizedUpdate!.Active, "active=false while mode 2026 is pending");
        Assert.IsNotNull(during.SynchronizedUpdate.StartedAtSequence);
        Assert.IsTrue(during.SynchronizedUpdate.StartedAtSequence <= Sequence(during));
        StringAssert.StartsWith(during.Content, "PART", "the partially applied update was not returned");

        Apply(terminal, "\x1b[?2026l");
        var after = diagnostics.Capture(new DiagnosticCaptureRequest());
        Assert.IsFalse(after.SynchronizedUpdate!.Active, "active=true after the update ended");
        Assert.IsNull(after.SynchronizedUpdate.StartedAtSequence);
    }

    [TestMethod]
    public async Task SynchronizedUpdate_RepeatedBeginKeepsItsStart()
    {
        await using var terminal = CreateTerminal(40, 6);
        var diagnostics = new TerminalDiagnostics(terminal, "sync");
        Apply(terminal, "\x1b[?2026hA");
        var first = diagnostics.Capture(new DiagnosticCaptureRequest()).SynchronizedUpdate!.StartedAtSequence;

        Apply(terminal, "\x1b[?2026hB");
        var second = diagnostics.Capture(new DiagnosticCaptureRequest());

        Assert.AreEqual(first, second.SynchronizedUpdate!.StartedAtSequence, "a repeated begin moved the update's start");
        Assert.IsTrue(second.SynchronizedUpdate.StartedAtSequence < Sequence(second));
    }

    [TestMethod]
    public async Task SynchronizedUpdate_ReleasedByTimeoutIsInactive()
    {
        await using var terminal = CreateTerminal(40, 6);
        var diagnostics = new TerminalDiagnostics(terminal, "sync");
        Apply(terminal, "\x1b[?2026hSTUCK");
        Assert.IsTrue(diagnostics.Capture(new DiagnosticCaptureRequest()).SynchronizedUpdate!.Active);

        await Task.Delay(TimeSpan.FromMilliseconds(1300), TestContext.Current.CancellationToken);

        Assert.IsFalse(diagnostics.Capture(new DiagnosticCaptureRequest()).SynchronizedUpdate!.Active,
            "the model released the update on its timeout, but the capture still reports it pending");
    }

    [TestMethod]
    public async Task Limitations_StateCoherenceScope()
    {
        await using var terminal = CreateTerminal(40, 6);
        var diagnostics = new TerminalDiagnostics(terminal, "limits");

        var result = diagnostics.Capture(new DiagnosticCaptureRequest());
        var capture = diagnostics.GetCapabilities().Operations.Single(o => o.Operation == "capture");

        foreach (var limitations in new[] { result.Limitations, capture.Limitations })
        {
            Assert.IsFalse(limitations.Any(l => l.Contains("not yet a declared guarantee", StringComparison.Ordinal)),
                "stale coherence limitation present");
            Assert.IsTrue(limitations.Any(l => l.Contains("One model read covers", StringComparison.Ordinal)),
                "the coherence scope is not stated");
            Assert.IsTrue(limitations.Any(l => l.Contains("not atomic with", StringComparison.Ordinal)),
                "cross-layer atomicity is not disclaimed");
            Assert.IsTrue(limitations.Any(l => l.Contains("native host", StringComparison.Ordinal)),
                "the model-only layer is not stated");
            Assert.IsTrue(limitations.Any(l => l.Contains("Graphics", StringComparison.Ordinal)),
                "graphics exclusion from the sequence is not stated");
        }
    }

    // === Helpers ===

    private static long Sequence(DiagnosticCaptureResult result)
    {
        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, result.Problem?.Message);
        Assert.IsNotNull(result.Identity, "identity is absent");
        return result.Identity.ModelSequence;
    }

    private static string Fingerprint(DiagnosticCaptureResult result) =>
        $"{result.Geometry!.Columns}x{result.Geometry.Rows}@{result.Geometry.CursorColumn},{result.Geometry.CursorRow}" +
        $"|h{result.History!.AvailableRows}/{result.History.ReturnedRows}|{result.Content}";

    private static void Apply(Hex1bTerminal terminal, string output) =>
        terminal.ApplyTokens(AnsiTokenizer.Tokenize(output));

    private static Hex1bTerminal CreateTerminal(int width, int height, int? retention = null, bool reflow = false)
    {
        var builder = Hex1bTerminal.CreateBuilder()
            .WithWorkload(new Hex1bAppWorkloadAdapter())
            .WithHeadless()
            .WithDimensions(width, height);
        if (retention is int capacity)
            builder.WithScrollback(capacity);
        if (reflow)
            builder.WithReflow();
        return builder.Build();
    }
}
