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
public class DiagnosticModelRestoreTests
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
    [DataRow("retained-history", "1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7\r\n8\r\n9\r\n10\r\n11\r\n12\r\n", "_scrollbackBuffer")]
    [DataRow("", "main\u001b[?1049halt", "_savedMainScreenBuffer")]
    [DataRow("titles", "\u001b]2;T\u0007", "_windowTitle")]
    [DataRow("titles", "\u001b]1;I\u0007", "_iconName")]
    [DataRow("titles", "\u001b]22;\u0007", "_titleStack")]
    [DataRow("command-marks", "\u001b]133;A\u0007$ ", "_commandMarks")]
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
        Assert.AreEqual("retained-history,titles,pending-input", string.Join(",", StartCheckpoint.Unsupported(model.CaptureModelState())));
    }

    [TestMethod]
    public void ModelRestore_RefusesStateItCannotRepresent()
    {
        var model = Detached(new FakeTimeProvider());
        model.ApplyRecordedOutput(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 14).Select(i => $"{i}\r\n"))));
        var replica = Detached(new FakeTimeProvider());
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(model.CaptureModelState()));
        StringAssert.Contains(error.Message, "retained history rows");
        Assert.AreEqual(0, replica.CurrentModelSequence, "a refused restore changed the model");
    }

    // The restore parses names as the projection writes them, with the bounds the manifest reader and the HMP1 row
    // codec enforce: a numeric enum name or an out-of-range standard colour index is refused, not coerced.
    [TestMethod]
    [DataRow("underline-style", "1")]
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

    private static Hex1bTerminal Detached(TimeProvider clock) => new(new Hex1bTerminalOptions
    {
        PresentationAdapter = new HeadlessPresentationAdapter(40, 10),
        WorkloadAdapter = new CaseReapplier.DetachedWorkload(),
        Width = 40,
        Height = 10,
        ScrollbackCapacity = 100,
        TimeProvider = clock,
        DeferStart = true,
    });

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
