using System.Reflection;
using System.Text;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

/// <summary>
/// Restoring a <c>text-state/1</c> projection into a detached model (ticket 09): the restored model projects
/// back equal, interprets later input as the original does, owns its hyperlinks and its synchronized-update
/// timer, and a start state's unsupported surfaces are named.
/// </summary>
[TestClass]
public partial class DiagnosticModelRestoreTests
{
    // Every supported surface at once: wide CJK and emoji at the right edge (wrap padding), two hyperlinks with
    // parameters (one target twice), protected cells, 256-colour and RGB styles, curly underline with colour, soft
    // wraps, margins, tabs, charsets, modes, saved cursor, activity, REP's last cell, and an open synchronized
    // update, all after a resize away from the configured geometry. No history, titles, marks or pending input.
    private const string Stress =
        "\u001b[38;5;202m\u001b[48;2;1;2;3mcolour\u001b[m \u001b[4:3m\u001b[58;2;9;8;7mcurly\u001b[24;59m " +
        "\u001b]8;id=a;https://x.test/one\u001b\\one\u001b]8;;\u001b\\ \u001b]8;id=a;https://x.test/one\u001b\\again\u001b]8;;\u001b\\ " +
        "\u001b]8;id=b;https://x.test/two\u001b\\two\u001b]8;;\u001b\\\r\n" +
        "\u001b[1\"q\u001b[1mprotected\u001b[0\"q\u001b[m\r\n" +
        "a line long enough to wrap past the right edge of the screen\r\n" +
        "\u001b[3;37H漢字👩‍💻x\r\n" +
        "\u001b[3g\u001b[1;5H\u001bH\u001b[1;13H\u001bH\u001b)0\u000e\u001b[2;9r\u001b[?69h\u001b[3;30s\u001b[?6h" +
        "\u001b[4;4H\u001b7\u001b[?1h\u001b[?2004h\u001b[?1000h\u001b[?1006h\u001b[4h\u001b[5q" +
        "\u001b]9;4;1;40\u0007\u001b]7;file:///probe/dir\u0007\u001b[3;32;44m\u001b]8;id=p;https://x.test/one\u001b\\" +
        "\u001b[2;28HQQ\u001b[?2026h";

    [TestMethod]
    public void ModelRestore_RoundTripsSupportedState()
    {
        var clock = new FakeTimeProvider();
        var original = Detached(clock);
        original.Resize(37, 9);
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(Stress));
        var state = original.CaptureModelState();
        Assert.IsEmpty(StartCheckpoint.Unsupported(state), "fixture: the stress state must be restorable");
        var cells = state.Screen.SelectMany(row => row.Cells).ToList();
        Assert.IsTrue(cells.Any(c => c.WideWrapPadding), "fixture: no wide-wrap padding");
        Assert.IsTrue(state.Styles.Any(s => s.Attributes.Contains("soft-wrap")) && state.Styles.Any(s => s.Attributes.Contains("protected")),
            "fixture: no soft wrap or protected cell");
        Assert.IsTrue(state.SynchronizedUpdate.Active && state.LastPrinted is not null && state.Rendition.HyperlinkUri is not null,
            "fixture: no open update, last printed cell or current hyperlink");

        var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(state);

        var differences = JsonDifferences(Json(state), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, "the restored model does not project back equal: " + string.Join("; ", differences.Take(10)));
        Assert.AreEqual(Digest(original), Digest(replica), "the restored model's public view differs");
        Assert.AreEqual((37, 9), (replica.Width, replica.Height));
        // Three distinct (target, parameters) pairs: one twice with id=a, two with id=b, and the rendition's one with id=p.
        Assert.AreEqual((3, 3), (Store(original).HyperlinkCount, Store(replica).HyperlinkCount),
            "the replica's store does not hold each restored hyperlink once");
    }

    // Per continuation field: X leaves it, Y reveals it (evidence P2). The start states must project equal, and
    // after Y the replica must equal the original.
    [TestMethod]
    [DataRow("last printed (REP)", "\u001b[1;1HQ", "\u001b[3b")]
    [DataRow("pending grapheme combine", "\u001b[2;1H👩‍", "💻!")]
    [DataRow("pending wrap", "\u001b[1;40HW", "Z")]
    [DataRow("current attributes", "\u001b[1;3m", "A")]
    [DataRow("current colours", "\u001b[32;44m", "A")]
    [DataRow("current underline", "\u001b[4:3m\u001b[58;5;9m", "A")]
    [DataRow("current hyperlink", "\u001b]8;id=p;https://x.test/a\u001b\\", "A")]
    [DataRow("cursor protection", "\u001b[1\"q", "P\u001b[1;1H\u001b[?2J")]
    [DataRow("protected mode", "\u001b[1\"q", "P\u001b[1;1H\u001b[?2J")]
    [DataRow("charset G1 + SO", "\u001b)0\u000e", "q")]
    [DataRow("charset G0", "\u001b(0", "q")]
    [DataRow("origin mode", "\u001b[3;8r\u001b[?6h", "\u001b[1;1HO")]
    [DataRow("scroll region", "\u001b[3;5r", "\u001b[5;1H\n\n\nS")]
    [DataRow("left/right margins", "\u001b[?69h\u001b[5;20s", "\u001b[3;10Habc\rL")]
    [DataRow("tab stops", "\u001b[3g\u001b[1;7H\u001bH", "\r\tT")]
    [DataRow("saved cursor", "\u001b[4;4H\u001b7\u001b[1;1H", "\u001b8S")]
    [DataRow("insert mode", "\u001b[1;1Habc\u001b[1;1H\u001b[4h", "I")]
    [DataRow("grapheme cluster mode off", "\u001b[?2027l", "\u001b[3;1H👩‍💻!")]
    [DataRow("wraparound off", "\u001b[?7l", "\u001b[4;35H0123456789")]
    [DataRow("newline mode", "\u001b[20h", "\u001b[5;5Ha\nb")]
    [DataRow("reverse wrap", "\u001b[?45h", "\u001b[6;1H\b\bR")]
    [DataRow("activity state", "\u001b]9;4;1;40\u0007\u001b]7;file:///probe/dir\u0007", "x")]
    [DataRow("input modes", "\u001b[?1h\u001b[?2004h\u001b[?1000h\u001b[?1006h", "x")]
    [DataRow("cursor shape + visibility", "\u001b[5q\u001b[?25l", "x")]
    public void ModelRestore_ContinuationRevealedByLaterInput(string field, string leave, string reveal)
    {
        var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(leave));
        var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(original.CaptureModelState());
        var start = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(start, $"{field}: the restored start differs: " + string.Join("; ", start.Take(5)));

        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(reveal));
        replica.ApplyRecordedOutput(Encoding.UTF8.GetBytes(reveal));
        var after = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(after, $"{field}: after the revealing input the restored model differs: " + string.Join("; ", after.Take(5)));
        Assert.AreEqual(Digest(original), Digest(replica), $"{field}: the public views differ after the revealing input");
    }

    // A start on the alternate screen (evidence P9): both screens restored, entered through the model's own entry, so
    // leaving it, resizing while on it and writing on it agree with the original.
    [TestMethod]
    [DataRow("leave", false, 0, 0, "\u001b[?1049lmain after")]
    [DataRow("resize then leave", false, 30, 8, "\u001b[?1049l after")]
    [DataRow("grow then leave", false, 50, 12, "\u001b[?1049l after")]
    [DataRow("write on it", false, 0, 0, "alt more\r\nnext")]
    [DataRow("resized before the start, grow then leave", true, 50, 12, "\u001b[?1049l after")]
    public void ModelRestore_AlternateScreenReproducesLeaving(string name, bool resizedBeforeStart, int width, int height, string reveal)
    {
        var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(
            "\u001b[1;32mmain text\u001b[m\r\n\u001b]8;;https://x.test/m\u001b\\linked\u001b]8;;\u001b\\\u001b[5;5Hcur\u001b[?1049h\u001b[Halt text"));
        if (resizedBeforeStart)
            original.Resize(30, 8);
        var state = original.CaptureModelState();
        Assert.IsNotNull(state.SavedMainScreen, "fixture: the start is not on the alternate screen");
        Assert.IsEmpty(StartCheckpoint.Unsupported(state), "fixture: the start is not restorable");

        var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(state);
        var start = JsonDifferences(Json(state), Json(replica.CaptureModelState()));
        Assert.IsEmpty(start, $"{name}: the restored start differs: " + string.Join("; ", start.Take(5)));
        // Oracle: the model's own alternate-screen bookkeeping, read directly.
        Assert.AreEqual(AlternateBookkeeping(original), AlternateBookkeeping(replica), $"{name}: the replica did not enter the alternate screen as the model does");

        if (width > 0)
        {
            original.Resize(width, height);
            replica.Resize(width, height);
        }
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(reveal));
        replica.ApplyRecordedOutput(Encoding.UTF8.GetBytes(reveal));
        var after = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(after, $"{name}: after the revealing input the restored model differs: " + string.Join("; ", after.Take(5)));
        Assert.AreEqual(Digest(original), Digest(replica), $"{name}: the public views differ after the revealing input");
    }

    // A shell that scrolled, then cleared its scrollback (ESC[3J), holds no retained rows but has advanced its row
    // identities: the restored history must continue them, so rows scrolled after the start compare equal.
    [TestMethod]
    public void ModelRestore_ClearedScrollbackContinuesRowIdentities()
    {
        var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 14).Select(i => $"{i}\r\n")) + "\u001b[H\u001b[2J\u001b[3J"));
        var state = original.CaptureModelState();
        Assert.AreEqual((0, true), (state.History?.Rows.Count ?? -1, state.History!.NextRowId > 1), "fixture: the scrollback was not cleared after advancing");
        Assert.IsEmpty(StartCheckpoint.Unsupported(state), "fixture: a cleared scrollback is restorable");

        var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(state);
        var start = JsonDifferences(Json(state), Json(replica.CaptureModelState()));
        Assert.IsEmpty(start, "the restored start differs: " + string.Join("; ", start.Take(5)));
        var scroll = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 12).Select(i => $"after {i}\r\n")));
        original.ApplyRecordedOutput(scroll);
        replica.ApplyRecordedOutput(scroll);
        var after = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(after, "rows scrolled after the start differ: " + string.Join("; ", after.Take(5)));
    }

    // Retained history (ticket 10): every row, its identity, original width, soft wrap and styles, in partial and full
    // rings, rows kept at a width other than the current one by a strategy that does not reflow, a single row, and wide
    // glyphs (with their continuations and wrap padding), and rows scrolled wider than 10,000 columns (a resize has no
    // upper width, and a model that does not reflow keeps them).
    [TestMethod]
    [DataRow("partial", 100, false)]
    [DataRow("full ring", 12, false)]
    [DataRow("off-width rows", 100, true)]
    [DataRow("one row", 100, false)]
    [DataRow("wide glyphs", 100, false)]
    [DataRow("wider than 10,000 columns", 100, false)]
    public void ModelRestore_HistoryRoundTrips(string shape, int capacity, bool resizedBeforeStart)
    {
        var original = Detached(new FakeTimeProvider(), capacity: capacity);
        if (shape == "wider than 10,000 columns")
            original.Resize(10_001, 3);
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(shape switch
        {
            "one row" => string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\r\n")),
            "wide glyphs" => HistoryLines(10) + string.Concat(Enumerable.Range(1, 20).Select(i => $"{i} \u8868\u793a\u5e45\u306e\u5e83\u3044\u6587\u5b57\u304c\u4e26\u3076\u884c \U0001F389\U0001F389 wide glyphs that wrap\r\n")),
            _ => HistoryLines(30),
        }));
        if (resizedBeforeStart)
            original.Resize(30, 10);
        if (shape == "wider than 10,000 columns")
            original.Resize(40, 10);
        var state = original.CaptureModelState();
        Assert.IsNotEmpty(state.History!.Rows, "fixture: no history");
        if (shape == "one row")
            Assert.HasCount(1, state.History.Rows, "fixture: not one row");
        else if (shape == "wider than 10,000 columns")
            Assert.IsTrue(state.History.Rows.Any(r => r.OriginalWidth == 10_001), "fixture: no row wider than 10,000");
        else
            Assert.IsTrue(state.Styles.Any(s => s.Attributes.Contains("soft-wrap")), "fixture: no soft wrap");
        if (shape == "wide glyphs")
            Assert.IsTrue(state.History.Rows.Any(r => r.Cells.Any(c => c.Text.Length == 0)), "fixture: no wide glyph in history");
        if (resizedBeforeStart)
            Assert.IsTrue(state.History.Rows.Any(r => r.OriginalWidth != state.Width), "fixture: no row at another width");

        var replica = Detached(new FakeTimeProvider(), capacity: capacity);
        replica.RestoreModelState(state);
        var differences = JsonDifferences(Json(state), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, $"{shape}: the restored history does not project back equal: " + string.Join("; ", differences.Take(5)));
        Assert.AreEqual(Digest(original), Digest(replica), $"{shape}: the public view (viewport and retained rows) differs");
        CollectionAssert.AreEqual(RingIds(original), RingIds(replica), $"{shape}: the ring's identities differ");
    }

    // Later output (with eviction), a shrink, a grow and leaving the alternate screen agree with the original under
    // every strategy a case can record (evidence P1: seven reflow history, three do not).
    [TestMethod]
    [DynamicData(nameof(StrategiesAndShapes))]
    public void ModelRestore_HistoryReflowsAsTheOriginal(string strategyId, string shape)
    {
        var strategy = CaseConfiguration.CreateReflowStrategy(strategyId)!;
        var capacity = shape == "full" ? 12 : 100;
        var original = Detached(new FakeTimeProvider(), strategy, capacity);
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(HistoryLines(30) + (shape == "alternate" ? "\u001b[?1049h\u001b[Halt text" : "")));
        var replica = Detached(new FakeTimeProvider(), strategy, capacity);
        replica.RestoreModelState(original.CaptureModelState());

        void Both(Action<Hex1bTerminal> step, string at)
        {
            step(original);
            step(replica);
            var after = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
            Assert.IsEmpty(after, $"{strategyId} {shape} {at}: " + string.Join("; ", after.Take(4)));
            Assert.AreEqual(Digest(original), Digest(replica), $"{strategyId} {shape} {at}: the public views differ");
        }

        Both(_ => { }, "start");
        Both(t => t.ApplyRecordedOutput(Encoding.UTF8.GetBytes(HistoryLines(8, "more"))), "scrolled");
        Both(t => t.Resize(24, 7), "shrunk");
        Both(t => t.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b[1;36mafter shrink with a line long enough to wrap\u001b[m\r\n")), "output");
        Both(t => t.Resize(70, 14), "grown");
        if (shape == "alternate")
            Both(t => t.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b[?1049lmain again\r\n")), "left the alternate screen");
    }

    // A recorded history the model cannot hold as recorded is refused before anything is applied (design C4, as amended).
    [TestMethod]
    [DataRow("ids not ascending")]
    [DataRow("a duplicate id")]
    [DataRow("more rows than the capacity")]
    [DataRow("another capacity")]
    [DataRow("next id not beyond the last")]
    [DataRow("absent with a scrollback")]
    [DataRow("present without a scrollback")]
    [DataRow("no rows")]
    [DataRow("a row without its id")]
    [DataRow("a row without its original width")]
    [DataRow("a row without cells")]
    [DataRow("a null row")]
    [DataRow("original width 0")]
    [DataRow("original width -3")]
    [DataRow("cells not the original width")]
    public void ModelRestore_RefusesMalformedHistory(string shape)
    {
        // The original ends at another geometry than the replica's, so a refusal after the restore's resize would show.
        var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(HistoryLines(20)));
        original.Resize(50, 12);
        var state = original.CaptureModelState();
        var history = state.History!;
        var rows = history.Rows.ToList();
        DiagnosticModelHistory? forged = shape switch
        {
            "ids not ascending" => history with { Rows = [rows[1], rows[0], .. rows.Skip(2)] },
            "a duplicate id" => history with { Rows = [rows[0], rows[1] with { Id = rows[0].Id }, .. rows.Skip(2)] },
            "more rows than the capacity" => history with { Capacity = rows.Count - 1 },
            "another capacity" => history with { Capacity = history.Capacity + 1 },
            "next id not beyond the last" => history with { NextRowId = rows[^1].Id!.Value },
            "absent with a scrollback" => null,
            "present without a scrollback" => history,
            "no rows" => history with { Rows = null! },
            "a row without its id" => history with { Rows = [rows[0] with { Id = null }, .. rows.Skip(1)] },
            "a row without its original width" => history with { Rows = [rows[0] with { OriginalWidth = null }, .. rows.Skip(1)] },
            "a row without cells" => history with { Rows = [rows[0] with { Cells = null! }, .. rows.Skip(1)] },
            "a null row" => history with { Rows = [null!, .. rows.Skip(1)] },
            "original width 0" => history with { Rows = [rows[0] with { OriginalWidth = 0, Cells = [] }, .. rows.Skip(1)] },
            "original width -3" => history with { Rows = [rows[0] with { OriginalWidth = -3, Cells = [] }, .. rows.Skip(1)] },
            _ => history with { Rows = [rows[0] with { Cells = [.. rows[0].Cells.Take(3)] }, .. rows.Skip(1)] },
        };
        var replica = Detached(new FakeTimeProvider(), capacity: shape switch
        {
            "more rows than the capacity" => rows.Count - 1,
            "present without a scrollback" => null,
            _ => 100,
        });
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(state with { History = forged }));
        StringAssert.Contains(error.Message, "history", shape);
        Assert.AreEqual((0L, 0, 40, 10), (replica.CurrentModelSequence, replica.ScrollbackCount, replica.Width, replica.Height),
            $"{shape}: a refused restore changed the model");
    }

    // History hyperlinks are the replica's own, one counted reference per cell: its store holds exactly the distinct
    // targets its cells reference, and none once every restored row is evicted and the screen cleared. (The original's
    // own count is not an oracle: scrolling under-counts it, .scratch/hex1b-diagnostics/issues/17.)
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ModelRestore_HistoryHyperlinksCountedOnce(bool linkOpenAtTheStart)
    {
        var original = Detached(new FakeTimeProvider(), capacity: 12);
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 16).Select(i =>
            $"\u001b]8;;https://x.test/{i % 3}\u001b\\link {i}\u001b]8;;\u001b\\\r\n"))
            + (linkOpenAtTheStart ? "\u001b]8;;https://x.test/open\u001b\\still open" : "")));
        var replica = Detached(new FakeTimeProvider(), capacity: 12);
        replica.RestoreModelState(original.CaptureModelState());
        Assert.AreEqual(linkOpenAtTheStart, OpenHyperlink(replica) is not null, "fixture: the rendition's link");
        var referenced = ReferencedTargets(replica);
        Assert.IsGreaterThanOrEqualTo(3, referenced.Count, "fixture: the restored cells reference fewer than three targets");
        Assert.IsTrue(replica.GetScrollbackRows(replica.ScrollbackCount).Any(r => r.Cells.Any(c => c.HyperlinkData is not null)), "fixture: no hyperlink in history");
        Assert.AreEqual(referenced.Count, Store(replica).HyperlinkCount, "the store does not hold exactly the targets its cells reference");
        // One counted reference per cell holding an object (no scroll has run yet, so issue 17 cannot interfere).
        foreach (var (tracked, cells) in CellsPerObject(replica))
            Assert.AreEqual(cells, tracked.RefCount, $"{tracked.Data.Uri}: {tracked.RefCount} references for {cells} cells");

        // Close the rendition's link, scroll every restored row out of the ring and clear the screen: nothing
        // references them, and none remain.
        replica.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b]8;;\u001b\\" + string.Concat(Enumerable.Range(1, 30).Select(i => $"plain {i}\r\n")) + "\u001b[2J"));
        Assert.AreEqual((0, 0), (ReferencedTargets(replica).Count, Store(replica).HyperlinkCount), "evicted history hyperlinks outlive their rows");
    }

    // Each tracked hyperlink object and how many cells hold it, through the public API (viewport and retained rows).
    private static Dictionary<TrackedObject<HyperlinkData>, int> CellsPerObject(Hex1bTerminal terminal)
    {
        var counts = new Dictionary<TrackedObject<HyperlinkData>, int>(ReferenceEqualityComparer.Instance);
        void Add(TrackedObject<HyperlinkData>? tracked)
        {
            if (tracked is not null)
                counts[tracked] = counts.GetValueOrDefault(tracked) + 1;
        }
        for (var y = 0; y < terminal.Height; y++)
            for (var x = 0; x < terminal.Width; x++)
                Add(terminal.GetTrackedHyperlinkAt(x, y));
        foreach (var row in terminal.GetScrollbackRows(terminal.ScrollbackCount))
            foreach (var cell in row.Cells)
                Add(cell.TrackedHyperlink);
        Add(OpenHyperlink(terminal));
        return counts;
    }

    // The rendition's open OSC 8 link (it holds one reference), read raw: it has no public accessor.
    private static TrackedObject<HyperlinkData>? OpenHyperlink(Hex1bTerminal terminal) =>
        (TrackedObject<HyperlinkData>?)typeof(Hex1bTerminal).GetField("_currentHyperlink", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(terminal);

    // The distinct hyperlink targets the model's cells reference, through the public API (viewport and retained rows).
    private static HashSet<string> ReferencedTargets(Hex1bTerminal terminal)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        void Add(HyperlinkData? data)
        {
            if (data is not null)
                targets.Add(data.Uri + "\u0000" + data.Parameters);
        }
        using (var snapshot = terminal.CreateSnapshot())
        {
            for (var y = 0; y < snapshot.Height; y++)
                for (var x = 0; x < snapshot.Width; x++)
                    Add(snapshot.GetCell(x, y).HyperlinkData);
        }
        foreach (var row in terminal.GetScrollbackRows(terminal.ScrollbackCount))
            foreach (var cell in row.Cells)
                Add(cell.HyperlinkData);
        Add(OpenHyperlink(terminal)?.Data);
        return targets;
    }

    [TestMethod]
    public void ModelRestore_HyperlinksLiveInReplicaStore()
    {
        var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(
            "\u001b]8;;https://x.test/one\u001b\\a\u001b]8;;\u001b\\ \u001b]8;;https://x.test/one\u001b\\b\u001b]8;;\u001b\\ \u001b]8;;https://x.test/two\u001b\\c\u001b]8;;\u001b\\"));
        var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(original.CaptureModelState());

        var store = Store(replica);
        Assert.AreEqual(2, store.HyperlinkCount, "the replica's store does not hold each restored target once");
        var first = replica.GetTrackedHyperlinkAt(0, 0);
        Assert.IsNotNull(first);
        Assert.AreSame(first, replica.GetTrackedHyperlinkAt(2, 0), "two cells with one target hold different objects");

        // A later hyperlink to the same target reuses the restored object.
        replica.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b[2;1H\u001b]8;;https://x.test/one\u001b\\d\u001b]8;;\u001b\\"));
        Assert.AreEqual(2, store.HyperlinkCount);
        Assert.AreSame(first, replica.GetTrackedHyperlinkAt(0, 1), "a later hyperlink to a restored target did not reuse it");
    }

    [TestMethod]
    public void ModelRestore_SynchronizedUpdateTimesOutOnReplicaClock()
    {
        var originalClock = new FakeTimeProvider();
        var original = Detached(originalClock);
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes("text\u001b[?2026hmore"));
        var replicaClock = new FakeTimeProvider();
        var replica = Detached(replicaClock);
        replica.RestoreModelState(original.CaptureModelState());
        var sequence = original.CurrentModelSequence;
        Assert.AreEqual(sequence, replica.CurrentModelSequence);

        originalClock.Advance(TimeSpan.FromMilliseconds(999));
        replicaClock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.AreEqual((sequence, sequence), (original.CurrentModelSequence, replica.CurrentModelSequence), "a timeout fired before 1 s");

        originalClock.Advance(TimeSpan.FromMilliseconds(1));
        replicaClock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual((sequence + 1, sequence + 1), (original.CurrentModelSequence, replica.CurrentModelSequence),
            "the restored update did not time out on the replica's clock");
        var after = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(after, "after the timeout the states differ: " + string.Join("; ", after.Take(5)));
    }

    // Each refused surface is named, checked against the private field that holds it (not the projection).
    [TestMethod]
    [DataRow("", "plain\r\n", "")]
    [DataRow("", "1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7\r\n8\r\n9\r\n10\r\n11\r\n12\r\n", "_scrollbackBuffer")]
    [DataRow("", "main\u001b[?1049halt", "_savedMainScreenBuffer")]
    // Titles and command marks are restored since ticket 11: controls, held but not named.
    [DataRow("", "\u001b]2;T\u0007", "_windowTitle")]
    [DataRow("", "\u001b]1;I\u0007", "_iconName")]
    [DataRow("", "\u001b]22;\u0007", "_titleStack")]
    [DataRow("", "\u001b]133;A\u0007$ ", "_commandMarks")]
    [DataRow("pending-input", "ok \u001b[", "_incompleteSequenceBuffer")]
    [DataRow("pending-input", "ok æ", "_pendingUtf8OutputLength")]
    [DataRow("dcs-continuation", "ok \u001bP1$r", "_dcsByteStreamParser")]
    [DataRow("graphics", "\u001bPq#0;2;100;0;0#0~~~~\u001b\\", "_sixelGraphicsState")]
    public void StartCheckpoint_RefusesEachSurface(string surface, string bytes, string field)
    {
        var model = Detached(new FakeTimeProvider());
        // "æ" stands for a UTF-8 scalar cut after its first two bytes.
        var data = bytes.EndsWith('æ') ? [.. Encoding.UTF8.GetBytes(bytes[..^1]), 0xe6, 0xbc] : Encoding.UTF8.GetBytes(bytes);
        model.ApplyRecordedOutput(data);
        var named = StartCheckpoint.Unsupported(model.CaptureModelState());
        Assert.AreEqual(surface, string.Join(",", named), "the named surfaces");
        if (field.Length > 0)
            Assert.IsTrue(Holds(model, field), $"oracle: the private field {field} does not hold the surface");
    }

    [TestMethod]
    public void StartCheckpoint_NamesEverySurfacePresent()
    {
        var model = Detached(new FakeTimeProvider());
        model.ApplyRecordedOutput([.. Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 14).Select(i => $"{i}\r\n")) + "\u001b]2;T\u0007ok "), 0xe6, 0xbc]);
        // Retained history (ticket 10) and titles (ticket 11) are restored; pending input is still named.
        Assert.AreEqual("pending-input", string.Join(",", StartCheckpoint.Unsupported(model.CaptureModelState())));
    }

    [TestMethod]
    public void ModelRestore_RefusesStateItCannotRepresent()
    {
        // Titles and command marks are restored since ticket 11; pending decoder bytes are not yet (ticket 12).
        var model = Detached(new FakeTimeProvider());
        model.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b]2;T\u0007ok \u001b["));
        var replica = Detached(new FakeTimeProvider());
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(model.CaptureModelState()));
        StringAssert.Contains(error.Message, "pending input");
        Assert.AreEqual(0, replica.CurrentModelSequence, "a refused restore changed the model");
    }

    // The restore parses names as the projection writes them, with the bounds the manifest reader and the HMP1 row
    // codec enforce: a numeric enum name or an out-of-range standard colour index is refused, not coerced.
    [TestMethod]
    [DataRow("underline-style", "1")]
    [DataRow("underline-style", "1-")]
    [DataRow("underline-style", "0-1")]
    [DataRow("underline-style", "single,double")]
    [DataRow("underline-style", "Curly")]
    [DataRow("foreground", "standard:9:#ffffff")]
    [DataRow("foreground", "bright:8:#ffffff")]
    public void ModelRestore_RefusesNamesOutsideTheContract(string field, string value)
    {
        var state = Detached(new FakeTimeProvider()).CaptureModelState();
        var rendition = field == "foreground" ? state.Rendition with { Foreground = value } : state.Rendition with { UnderlineStyle = value };
        var replica = Detached(new FakeTimeProvider());
        Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(state with { Rendition = rendition }));
        // Controls: the names the projection writes are accepted.
        Detached(new FakeTimeProvider()).RestoreModelState(state with { Rendition = rendition with { Foreground = "standard:7:#c0c0c0", UnderlineStyle = "curly" } });
    }

    private static Hex1bTerminal Detached(TimeProvider clock, Hex1b.Reflow.ITerminalReflowProvider? strategy = null, int? capacity = 100) =>
        new(new Hex1bTerminalOptions
        {
            PresentationAdapter = strategy is null
                ? new HeadlessPresentationAdapter(40, 10)
                : new HeadlessPresentationAdapter(40, 10).WithReflowStrategy(strategy, enabled: true),
            WorkloadAdapter = new CaseReapplier.DetachedWorkload(),
            Width = 40,
            Height = 10,
            ScrollbackCapacity = capacity,
            TimeProvider = clock,
            DeferStart = true,
        });

    // Styled lines, every third long enough to soft-wrap past the right edge.
    private static string HistoryLines(int lines, string prefix = "row") => string.Concat(Enumerable.Range(1, lines).Select(i =>
        i % 3 == 0
            ? $"\u001b[{31 + i % 6}m{prefix} {i} is a styled line long enough to soft-wrap past the right edge of the screen\u001b[m\r\n"
            : $"{prefix} {i} \u001b[1mbold\u001b[m plain\r\n"));

    // The retained rows' identities, read from the ring itself (no projection): the oracle for ids.
    private static long[] RingIds(Hex1bTerminal terminal)
    {
        var buffer = typeof(Hex1bTerminal).GetField("_scrollbackBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal);
        if (buffer is null)
            return [];
        var count = (int)buffer.GetType().GetProperty("Count")!.GetValue(buffer)!;
        var entries = (Array)buffer.GetType().GetMethod("GetEntries", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(buffer, [count])!;
        return entries.Cast<object>().Select(e => (long)e.GetType().GetProperty("RowId")!.GetValue(e)!).ToArray();
    }

    public static IEnumerable<object[]> StrategiesAndShapes() =>
        from strategy in CaseConfiguration.StrategyIds
        from shape in new[] { "partial", "full", "alternate" }
        select new object[] { strategy, shape };

    // Where the model records that the alternate screen is selected: both graphics states and the saved main text
    // coordinates (evidence P9).
    private static string AlternateBookkeeping(Hex1bTerminal terminal)
    {
        object? Field(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
        var kgp = Field(terminal, "_kgpGraphicsState")!;
        var sixel = Field(terminal, "_sixelGraphicsState")!;
        return $"kgp={Field(kgp, "_alternateActive")} sixel={Field(sixel, "_alternateActive")} savedRows={Field(terminal, "_savedMainTextRowIds") is not null}";
    }

    private static TrackedObjectStore Store(Hex1bTerminal terminal) =>
        (TrackedObjectStore)typeof(Hex1bTerminal).GetField("_trackedObjects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;

    // The oracle for classification: the private field that holds each surface.
    private static bool Holds(Hex1bTerminal terminal, string field)
    {
        var value = typeof(Hex1bTerminal).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal);
        return value switch
        {
            null => false,
            string s => s.Length > 0,
            int n => n > 0,
            System.Collections.ICollection c => c.Count > 0,
            ScrollbackBuffer history => history.Count > 0,
            _ when field == "_dcsByteStreamParser" => (bool)value.GetType().GetProperty("IsInDcs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value)!,
            _ when field == "_sixelGraphicsState" => (bool)value.GetType().GetProperty("HasResidentState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value)!,
            _ => true,
        };
    }

    // The oracle's view: the public snapshot only (no projection code). Cells with rendition and hyperlink, cursor,
    // pending wrap, protection, modes, geometry and the model sequence.
    private static string Digest(Hex1bTerminal terminal)
    {
        using var snapshot = terminal.CreateSnapshot();
        var builder = new StringBuilder();
        builder.Append($"{snapshot.Width}x{snapshot.Height} cursor={snapshot.CursorX},{snapshot.CursorY} pw={terminal.PendingWrap} ");
        builder.Append($"prot={terminal.CursorProtected} vis={snapshot.CursorVisible} shape={snapshot.CursorShape} modes={terminal.InputModes} seq={terminal.CurrentModelSequence}\n");
        for (var y = 0; y < snapshot.Height; y++)
        {
            for (var x = 0; x < snapshot.Width; x++)
            {
                var cell = snapshot.GetCell(x, y);
                builder.Append(cell.Character).Append('/').Append(cell.Foreground).Append('/').Append(cell.Background).Append('/')
                    .Append(cell.Attributes).Append('/').Append(cell.UnderlineStyle).Append('/').Append(cell.UnderlineColor).Append('/')
                    .Append(cell.HyperlinkData?.Uri).Append('|');
            }
            builder.Append('\n');
        }
        foreach (var row in terminal.GetScrollbackRows(terminal.ScrollbackCount))
        {
            builder.Append('[').Append(row.OriginalWidth).Append(']');
            foreach (var cell in row.Cells)
                builder.Append(cell.Character).Append('/').Append(cell.Foreground).Append('/').Append(cell.Attributes).Append('/')
                    .Append(cell.HyperlinkData?.Uri).Append('|');
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static JsonElement Json(DiagnosticModelState state) =>
        JsonSerializer.SerializeToElement(state, DiagnosticsJsonContext.Default.DiagnosticModelState);

    // An independent structural diff of two JSON documents (the comparer is not used).
    private static List<string> JsonDifferences(JsonElement a, JsonElement b, string path = "$")
    {
        var differences = new List<string>();
        if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
        {
            var keys = a.EnumerateObject().Select(p => p.Name).Union(b.EnumerateObject().Select(p => p.Name));
            foreach (var key in keys)
            {
                var hasA = a.TryGetProperty(key, out var va);
                var hasB = b.TryGetProperty(key, out var vb);
                if (hasA != hasB)
                    differences.Add($"{path}.{key} present {hasA} vs {hasB}");
                else
                    differences.AddRange(JsonDifferences(va, vb, $"{path}.{key}"));
            }
        }
        else if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array)
        {
            var la = a.GetArrayLength();
            var lb = b.GetArrayLength();
            if (la != lb)
                differences.Add($"{path} count {la} vs {lb}");
            for (var i = 0; i < Math.Min(la, lb); i++)
                differences.AddRange(JsonDifferences(a[i], b[i], $"{path}[{i}]"));
        }
        else if (a.GetRawText() != b.GetRawText())
        {
            differences.Add($"{path} {a.GetRawText()} vs {b.GetRawText()}");
        }
        return differences;
    }
}
