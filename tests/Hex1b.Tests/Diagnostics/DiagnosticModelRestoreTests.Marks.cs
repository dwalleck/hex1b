using System.Collections;
using System.Reflection;
using System.Text;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

// Ticket 11: titles, the title stack and OSC 133 command marks are restored, marks at their positions (buffer, row,
// column), and behave afterwards as the original's. The oracle for marks is the HWT1 viewer's resolution of anchors
// (CaptureMarkers) with the public CommandMarks; for titles, the public title and icon after each OSC 23 pop.
public partial class DiagnosticModelRestoreTests
{
    [TestMethod]
    public void ModelRestore_TitlesRoundTripAndPop()
    {
        // A stack five deep with duplicates, Unicode, push-with-set, and icon-only changes.
        var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(
            "\u001b]0;t1\u0007\u001b]22;\u0007\u001b]0;t2\u0007\u001b]1;icon 2\u0007\u001b]22;\u0007\u001b]22;\u0007"
            + "\u001b]2;t3 ünï漢\u0007\u001b]22;pushed\u0007\u001b]22;\u0007\u001b]1;last icon\u0007"));
        var state = original.CaptureModelState();
        Assert.AreEqual(5, state.Titles.Stack.Count, "fixture: the stack depth");
        var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(state);
        var differences = JsonDifferences(Json(state), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, "the restored titles do not project back equal: " + string.Join("; ", differences.Take(5)));

        // Behaviour, not the projection: every pop (past the stack's end too) and a push-with-set give the same titles.
        for (var i = 0; i < 8; i++)
        {
            var step = i == 3 ? "\u001b]22;mid\u0007\u001b]23;\u0007" : "\u001b]23;\u0007";
            original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(step));
            replica.ApplyRecordedOutput(Encoding.UTF8.GetBytes(step));
            Assert.AreEqual((original.WindowTitle, original.IconName), (replica.WindowTitle, replica.IconName), $"after pop {i + 1}");
        }
    }

    [TestMethod]
    [DynamicData(nameof(MarkScenarioNames))]
    public void ModelRestore_CommandMarksRoundTrip(string scenario)
    {
        var shape = MarkScenarios[scenario];
        var original = MarkModel(shape, "none");
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(shape.Before));
        var state = original.CaptureModelState();
        Assert.IsNotEmpty(state.CommandMarks, "fixture: no marks");
        var replica = MarkModel(shape, "none");
        replica.RestoreModelState(state);

        var differences = JsonDifferences(Json(state), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, $"{scenario}: the restored marks do not project back equal: " + string.Join("; ", differences.Take(5)));
        Assert.AreEqual(ViewerMarks(original), ViewerMarks(replica), $"{scenario}: the viewer resolves the marks differently");
        foreach (var region in shape.Regions)
            Assert.IsTrue(InRegion(state, region), $"fixture: {scenario} has no mark in {region}");
    }

    // The probe's scenarios (evidence P1) under each strategy: every later step leaves both models with the same
    // projection (marks' positions included) and the same viewer marks, replayed to each step on a fresh pair
    // because the viewer's resolution moves and collects anchors.
    [TestMethod]
    [DynamicData(nameof(MarkScenariosAndStrategies))]
    public void ModelRestore_CommandMarksBehaveAsTheOriginal(string scenario, string strategy)
    {
        var shape = MarkScenarios[scenario];
        var (original, replica) = RestoredPair(shape, strategy);
        foreach (var (name, input) in shape.Steps)
        {
            ApplyStep(original, input);
            ApplyStep(replica, input);
            var differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
            Assert.IsEmpty(differences, $"{scenario} {strategy} {name}: " + string.Join("; ", differences.Take(4)));
        }

        for (var upTo = 0; upTo <= shape.Steps.Count; upTo++)
        {
            var (a, b) = RestoredPair(shape, strategy);
            foreach (var (_, input) in shape.Steps.Take(upTo))
            {
                ApplyStep(a, input);
                ApplyStep(b, input);
            }
            Assert.AreEqual(ViewerMarks(a), ViewerMarks(b), $"{scenario} {strategy}: the viewer's marks after {upTo} steps");
        }
    }

    [TestMethod]
    public void ModelRestore_ProjectionLeavesMarksUnchanged()
    {
        // Twin originals; one is projected before every step. A projection that moved an anchor (a DECLRMM trailing
        // mark, which the viewer would resolve onto the next row) changes when the mark is lost.
        var shape = MarkScenarios["declrmm trailing"];
        var projected = MarkModel(shape, "ghostty");
        var untouched = MarkModel(shape, "ghostty");
        projected.ApplyRecordedOutput(Encoding.UTF8.GetBytes(shape.Before));
        untouched.ApplyRecordedOutput(Encoding.UTF8.GetBytes(shape.Before));
        foreach (var (name, input) in shape.Steps)
        {
            projected.CaptureModelState();
            ApplyStep(projected, input);
            ApplyStep(untouched, input);
            Assert.AreEqual(untouched.CommandMarks.Count, projected.CommandMarks.Count, $"{name}: a projected model kept or lost a mark");
        }
        var differences = JsonDifferences(Json(untouched.CaptureModelState()), Json(projected.CaptureModelState()));
        Assert.IsEmpty(differences, "projecting changed the model: " + string.Join("; ", differences.Take(4)));
    }

    [TestMethod]
    public void ModelRestore_ExpiredMarkRestoredExpired()
    {
        // A viewer's resolution can null an anchor between applications; the mark stays until the next collection.
        var shape = MarkScenarios["screen and history"];
        var original = MarkModel(shape, "none");
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(shape.Before));
        var count = original.CommandMarks.Count;
        var anchors = (IDictionary)PrivateField(original, "_commandAnchors");
        var expired = (TerminalTextAnchor)anchors[original.CommandMarks[1]]!;
        expired.RowId = null;
        SetPrivateField(original, "_textAnchorRetentionChanged", true);

        var state = original.CaptureModelState();
        Assert.AreEqual((null, null), (state.CommandMarks[1].Row, state.CommandMarks[1].Column), "the expired mark has a position");
        var replica = MarkModel(shape, "none");
        replica.RestoreModelState(state);
        Assert.IsEmpty(JsonDifferences(Json(state), Json(replica.CaptureModelState())), "the expired mark did not round-trip");

        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes("x"));
        replica.ApplyRecordedOutput(Encoding.UTF8.GetBytes("x"));
        Assert.AreEqual((count - 1, count - 1), (original.CommandMarks.Count, replica.CommandMarks.Count), "the expired mark was not collected in both");
        var after = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(after, "after the collection: " + string.Join("; ", after.Take(4)));
    }

    // A recorded state whose marks or titles are malformed is refused before anything is applied (design C7).
    [TestMethod]
    [DataRow("unknown phase")]
    [DataRow("anchor not command:n")]
    [DataRow("duplicate anchor")]
    [DataRow("anchor above the last")]
    [DataRow("unknown buffer")]
    [DataRow("alternate mark on main")]
    [DataRow("row outside the buffer")]
    [DataRow("negative row")]
    [DataRow("column past the width")]
    [DataRow("negative column")]
    [DataRow("row without column")]
    [DataRow("null mark")]
    [DataRow("null marks")]
    [DataRow("more marks than the capacity")]
    [DataRow("null title")]
    [DataRow("null stack entry field")]
    public void ModelRestore_RefusesMalformedMarksAndTitles(string shape)
    {
        // The original ends at another geometry than the replica's, so a refusal after the restore's resize would show.
        var original = MarkModel(MarkScenarios["screen and history"], "none");
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(MarkScenarios["screen and history"].Before + "\u001b]0;t\u0007\u001b]22;\u0007"));
        original.Resize(50, 12);
        var state = original.CaptureModelState();
        var marks = state.CommandMarks.ToList();
        Assert.IsGreaterThanOrEqualTo(3, marks.Count, "fixture: marks");
        var historyRows = state.History!.Rows.Count;
        List<DiagnosticModelCommandMark> With(int i, Func<DiagnosticModelCommandMark, DiagnosticModelCommandMark> change) =>
            [.. marks.Select((m, j) => j == i ? change(m) : m)];
        var forged = shape switch
        {
            "unknown phase" => state with { CommandMarks = With(0, m => m with { Phase = "Prompt" }) },
            "anchor not command:n" => state with { CommandMarks = With(0, m => m with { Anchor = "custom:1" }) },
            "duplicate anchor" => state with { CommandMarks = With(1, m => m with { Anchor = marks[0].Anchor }) },
            "anchor above the last" => state with { LastCommandAnchorId = long.Parse(marks[^1].Anchor[8..]) - 1 },
            "unknown buffer" => state with { CommandMarks = With(0, m => m with { Buffer = "saved" }) },
            "alternate mark on main" => state with { CommandMarks = With(0, m => m with { Buffer = "alternate", Row = 0, Column = 0 }) },
            "row outside the buffer" => state with { CommandMarks = With(0, m => m with { Row = historyRows + state.Height }) },
            "negative row" => state with { CommandMarks = With(0, m => m with { Row = -1 }) },
            "column past the width" => state with { CommandMarks = With(0, m => m with { Column = 51 }) },
            "negative column" => state with { CommandMarks = With(0, m => m with { Column = -1 }) },
            "row without column" => state with { CommandMarks = With(0, m => m with { Column = null }) },
            "null mark" => state with { CommandMarks = With(0, _ => null!) },
            "null marks" => state with { CommandMarks = null! },
            "more marks than the capacity" => state,
            "null title" => state with { Titles = state.Titles with { Window = null! } },
            _ => state with { Titles = state.Titles with { Stack = [state.Titles.Stack[0] with { Icon = null! }] } },
        };
        var replica = MarkModel(MarkScenarios["screen and history"], "none", markCapacity: shape == "more marks than the capacity" ? marks.Count - 1 : null);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(forged));
        StringAssert.Contains(error.Message, shape.Contains("title") || shape.Contains("stack") ? "titles" : "command mark", shape);
        Assert.AreEqual((0L, 40, 8, 0, ""), (replica.CurrentModelSequence, replica.Width, replica.Height, replica.CommandMarks.Count, replica.WindowTitle),
            $"{shape}: a refused restore changed the model");
    }

    // Scenarios: the probe's (evidence.md P1), with the strengthened fixtures.
    private sealed record MarkScenario(int Width, int Height, int Capacity, string Before, List<(string Name, string Input)> Steps, string[] Regions);

    private static readonly Dictionary<string, MarkScenario> MarkScenarios = new()
    {
        // Early marks are evicted before the start (gapped ids); the first step scrolls one row.
        ["screen and history"] = new(40, 8, 12, MarkPrompts(1, 3) + MarkLines(12) + MarkPrompts(4, 6),
            [("one line", "x\r\n"), ("more prompts", MarkPrompts(7, 10)), ("evict", MarkLines(10) + MarkPrompts(11, 14)),
             ("shrink", "RESIZE 24 6"), ("grow", "RESIZE 60 10"), ("erase screen", "\u001b[2J" + MarkPrompt(20, 0)),
             ("clear history", "\u001b[3J" + MarkPrompt(21, 1)),
             ("alternate round trip", "\u001b[?1049h" + MarkPrompt(22, 0) + "\u001b[?1049l" + MarkPrompt(23, 2))],
            ["main-history", "main-screen"]),
        // Marks on the saved main screen and in history while the alternate screen is active, and on the alternate.
        ["alternate"] = new(40, 8, 20, MarkPrompts(1, 3) + "\u001b[?1049h\u001b[H" + MarkPrompt(4, 0),
            [("alternate prompt", MarkPrompt(5, 1)), ("shrink", "RESIZE 30 6"), ("leave", "\u001b[?1049l"), ("prompt", MarkPrompt(6, 0))],
            ["main-history", "main-screen", "alternate"]),
        // Left/right margin mode: a wrap does not commit the trailing mark to the next row (evidence P2).
        ["declrmm trailing"] = new(20, 4, 3, "\u001b[?69h" + new string('x', 20) + "\u001b]133;A\u0007@T rest\r\n",
            [.. Enumerable.Range(1, 6).Select(i => ($"line {i}", $"l{i}\r\n"))], ["main-screen"]),
        ["insert delete"] = new(40, 8, 20, "abc\u001b]133;A\u0007def\u001b]133;B\u0007ghi\r\n",
            [("ICH", "\u001b[1;2H\u001b[3@"), ("DCH", "\u001b[1;2H\u001b[2P"), ("ICH past the edge", "\u001b[1;1H\u001b[39@"),
             ("prompt", "\u001b[5;1H" + MarkPrompt(9, 0))], ["main-screen"]),
        // A wide glyph before a mark: the anchor's column is the owning glyph's.
        ["wide glyph"] = new(20, 6, 20, "漢漢\u001b]133;A\u0007$ 漢\r\n" + MarkPrompt(2, 0),
            [("shrink", "RESIZE 9 6"), ("grow", "RESIZE 30 6"), ("evict", MarkLines(30))], ["main-screen"]),
    };

    public static IEnumerable<object[]> MarkScenarioNames() => MarkScenarios.Keys.Select(k => new object[] { k });

    public static IEnumerable<object[]> MarkScenariosAndStrategies() =>
        from scenario in MarkScenarios.Keys
        from strategy in new[] { "none", "ghostty", "xterm", "kitty" }
        select new object[] { scenario, strategy };

    private static (Hex1bTerminal Original, Hex1bTerminal Replica) RestoredPair(MarkScenario shape, string strategy)
    {
        var original = MarkModel(shape, strategy);
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(shape.Before));
        var replica = MarkModel(shape, strategy);
        replica.RestoreModelState(original.CaptureModelState());
        return (original, replica);
    }

    private static Hex1bTerminal MarkModel(MarkScenario shape, string strategy, int? markCapacity = null)
    {
        var presentation = strategy == "none"
            ? new HeadlessPresentationAdapter(shape.Width, shape.Height)
            : new HeadlessPresentationAdapter(shape.Width, shape.Height).WithReflowStrategy(CaseConfiguration.CreateReflowStrategy(strategy)!, enabled: true);
        var options = new Hex1bTerminalOptions
        {
            PresentationAdapter = presentation,
            WorkloadAdapter = new CaseReapplier.DetachedWorkload(),
            Width = shape.Width,
            Height = shape.Height,
            ScrollbackCapacity = shape.Capacity,
            TimeProvider = new FakeTimeProvider(),
            DeferStart = true,
        };
        if (markCapacity is { } capacity)
            options.CommandMarkHistoryCapacity = capacity;
        // The refusal fixture compares geometry with the default detached size.
        return new Hex1bTerminal(options);
    }

    private static void ApplyStep(Hex1bTerminal terminal, string input)
    {
        if (input.StartsWith("RESIZE ", StringComparison.Ordinal))
        {
            var parts = input.Split(' ');
            terminal.Resize(int.Parse(parts[1]), int.Parse(parts[2]));
            return;
        }
        terminal.ApplyRecordedOutput(Encoding.UTF8.GetBytes(input));
    }

    // The viewer's markers (buffer, resolved row, column, phase, exit) and the public marks, read through a path
    // independent of the projection. It resolves and collects anchors, so call it only at a comparison's end.
    private static string ViewerMarks(Hex1bTerminal terminal)
    {
        var buffer = typeof(Hex1bTerminal).GetMethod("GetTextBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(terminal, null)!;
        var markers = (Array)typeof(Hex1bTerminal).GetMethod("CaptureMarkers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(terminal, [new Hwt1ViewState(), buffer])!;
        var builder = new StringBuilder();
        foreach (var marker in markers)
            builder.Append(marker).Append('|');
        foreach (var mark in terminal.CommandMarks)
            builder.Append($"{mark.Phase}/{mark.ExitCode}/{mark.RawParameters}|");
        return builder.ToString();
    }

    private static bool InRegion(DiagnosticModelState state, string region)
    {
        var history = state.History?.Rows.Count ?? 0;
        return state.CommandMarks.Any(m => region switch
        {
            "main-history" => m.Buffer == "main" && m.Row < history,
            "main-screen" => m.Buffer == "main" && m.Row >= history,
            _ => m.Buffer == "alternate" && m.Row is not null,
        });
    }

    private static object PrivateField(Hex1bTerminal terminal, string name) =>
        typeof(Hex1bTerminal).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;

    private static void SetPrivateField(Hex1bTerminal terminal, string name, object value) =>
        typeof(Hex1bTerminal).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(terminal, value);

    private static string MarkPrompt(int i, int exit) =>
        $"\u001b]133;A\u0007@A{i:D2}$ \u001b]133;B\u0007cmd{i}\r\n\u001b]133;C;cmdline_url=cmd{i}\u0007out {i} one\r\nout {i} two\r\n\u001b]133;D;{exit}\u0007";

    private static string MarkPrompts(int from, int to) => string.Concat(Enumerable.Range(from, to - from + 1).Select(i =>
        (i % 2 == 1 ? "\u001b]22;\u0007" : "") + $"\u001b]0;title {i}\u0007" + MarkPrompt(i, i % 3)));

    private static string MarkLines(int n) => string.Concat(Enumerable.Range(1, n).Select(i => $"line {i}\r\n"));
}
