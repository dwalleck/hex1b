using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
        if (scenario == "screen and history")
            Assert.IsTrue(state.CommandMarks.Any(m => m.Phase == "finished" && m.ExitCode is null), "fixture: no finished mark without an exit code");
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
    [DataRow("null history row under a mark")]
    [DataRow("null history cells under a mark")]
    [DataRow("last anchor id at the maximum")]
    [DataRow("negative last anchor id")]
    [DataRow("last anchor id of -1")]
    [DataRow("column past a narrower history row")]
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
        var historyMark = marks.FindIndex(m => m.Row < historyRows);
        Assert.IsGreaterThanOrEqualTo(0, historyMark, "fixture: no mark in history");
        var markedRow = marks[historyMark].Row!.Value;
        List<DiagnosticModelRow> HistoryWith(Func<DiagnosticModelRow, DiagnosticModelRow> change) =>
            [.. state.History!.Rows.Select((r, j) => j == markedRow ? change(r) : r)];
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
            "null history row under a mark" => state with { History = state.History with { Rows = HistoryWith(_ => null!) } },
            "null history cells under a mark" => state with { History = state.History with { Rows = HistoryWith(r => r with { Cells = null! }) } },
            "last anchor id at the maximum" => state with { CommandMarks = [], LastCommandAnchorId = long.MaxValue },
            "negative last anchor id" => state with { CommandMarks = [], LastCommandAnchorId = -4 },
            "last anchor id of -1" => state with { CommandMarks = [], LastCommandAnchorId = -1 },
            // History rows keep the 40 columns they were written at, beside a 50-column screen.
            "column past a narrower history row" => state with { CommandMarks = With(historyMark, m => m with { Column = 45 }) },
            _ => state with { Titles = state.Titles with { Stack = [state.Titles.Stack[0] with { Icon = null! }] } },
        };
        var replica = MarkModel(MarkScenarios["screen and history"], "none", markCapacity: shape == "more marks than the capacity" ? marks.Count - 1 : null);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(forged));
        StringAssert.Contains(error.Message, shape.Contains("title") || shape.Contains("stack") ? "titles"
            : shape.StartsWith("null history", StringComparison.Ordinal) ? "history"
            : shape.Contains("anchor id") ? "last command anchor id" : "command mark", shape);
        Assert.AreEqual((0L, 40, 8, 0, ""), (replica.CurrentModelSequence, replica.Width, replica.Height, replica.CommandMarks.Count, replica.WindowTitle),
            $"{shape}: a refused restore changed the model");
    }

    [TestMethod]
    public void ModelRestore_RefusesAMarkPastItsSavedMainRow()
    {
        // On the alternate screen the saved main screen keeps its own width (40) beside a wider alternate screen (50):
        // a main mark on a saved-main row is checked against that row, not the alternate screen's.
        var shape = MarkScenarios["alternate"];
        var original = MarkModel(shape, "none");
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(shape.Before));
        original.Resize(50, 12);
        var state = original.CaptureModelState();
        var history = state.History?.Rows.Count ?? 0;
        var index = state.CommandMarks.ToList().FindIndex(m => m.Buffer == "main" && m.Row >= history);
        Assert.AreEqual((40, 50), (state.SavedMainScreen![0].Cells.Count, state.Width), "fixture: the saved main screen's width");
        Assert.IsGreaterThanOrEqualTo(0, index, "fixture: no mark on the saved main screen");
        var forged = state with { CommandMarks = [.. state.CommandMarks.Select((m, j) => j == index ? m with { Column = 45 } : m)] };
        var replica = MarkModel(shape, "none");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(forged));
        StringAssert.Contains(error.Message, "command mark");
        Assert.AreEqual((0L, 0), (replica.CurrentModelSequence, replica.CommandMarks.Count), "a refused restore changed the model");
    }

    [TestMethod]
    public void ModelRestore_RefusesAnAlternateMarkPastItsRow()
    {
        var shape = MarkScenarios["alternate"];
        var original = MarkModel(shape, "none");
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(shape.Before));
        var state = original.CaptureModelState();
        var index = state.CommandMarks.ToList().FindIndex(m => m.Buffer == "alternate" && m.Row is not null);
        Assert.IsGreaterThanOrEqualTo(0, index, "fixture: no mark on the alternate screen");
        var forged = state with { CommandMarks = [.. state.CommandMarks.Select((m, j) => j == index ? m with { Column = state.Width + 1 } : m)] };
        var replica = MarkModel(shape, "none");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(forged));
        StringAssert.Contains(error.Message, "command mark");
        Assert.AreEqual((0L, 0), (replica.CurrentModelSequence, replica.CommandMarks.Count), "a refused restore changed the model");
    }

    [TestMethod]
    public void ModelRestore_LastAnchorIdWithoutMarks()
    {
        // Marks collected by clearing leave the last anchor id: a restored model numbers its next mark after it.
        var shape = MarkScenarios["insert delete"];
        var original = MarkModel(shape, "none");
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(shape.Before + "\u001b[2J\u001b[3J\u001b[H"));
        var state = original.CaptureModelState();
        Assert.AreEqual((0, 2L), (state.CommandMarks.Count, state.LastCommandAnchorId), "fixture: no marks, a positive last id");
        var replica = MarkModel(shape, "none");
        replica.RestoreModelState(state);
        foreach (var t in new[] { original, replica })
            t.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b]133;A\u0007$ "));
        var differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, "the next mark is numbered differently: " + string.Join("; ", differences.Take(3)));
    }

    [TestMethod]
    public void ModelRestore_OrphanedContinuationKeepsItsColumn()
    {
        // A left/right-margin scroll removes a wide glyph's first cell and leaves its continuation (review XR#2). A mark
        // placed there after the start stays on it in both models: the restored continuation does not share its left
        // neighbour's write sequence, so it is not taken for that glyph's second cell.
        Hex1bTerminal Model()
        {
            var options = new Hex1bTerminalOptions
            {
                PresentationAdapter = new HeadlessPresentationAdapter(22, 7).WithReflowStrategy(CaseConfiguration.CreateReflowStrategy("ghostty")!, enabled: true),
                WorkloadAdapter = new CaseReapplier.DetachedWorkload(),
                Width = 22,
                Height = 7,
                CommandMarkHistoryCapacity = 3,
                TimeProvider = new FakeTimeProvider(),
                DeferStart = true,
            };
            return new Hex1bTerminal(options);
        }
        var original = Model();
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(
            "\t\u001b]133;A\u0007\t\u6f22\u5b57x\u001b]133;B\u0007\u6f22\u001b[1T\u001b[?69h\u001b[1;19s\u001b[2S"));
        var replica = Model();
        replica.RestoreModelState(original.CaptureModelState());
        foreach (var step in new[] { "\u001b[?7l", "\rpppppppppppppppppppppp\u001b]133;B\u0007q", "\u001bD\u001bE", "\t\u001b]133;A\u0007\t" })
        {
            original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(step));
            replica.ApplyRecordedOutput(Encoding.UTF8.GetBytes(step));
            var differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
            Assert.IsEmpty(differences, $"after {JsonSerializer.Serialize(step)}: " + string.Join("; ", differences.Take(3)));
        }
    }

    // Which empty cells continue the glyph to their left is projected (the model's own rule: a shared write sequence), so
    // a restored row reads as the original's for marks placed later, whatever made the glyph wide or left the cell
    // behind (review round 2: RR#1, RR#2).
    [TestMethod]
    [DataRow("vs16 applied later", 20, 4, true, "ab\u2764\u001b[m\uFE0F", false)]
    [DataRow("2027 conjunct", 20, 4, false, "ab\u0915\u094D\u0937", false)]
    [DataRow("2027 spacing mark", 20, 4, false, "ab\u0915\u093F", true)]
    [DataRow("2027 zwj text", 20, 4, false, "ab\u261D\u200D\u261D", true)]
    [DataRow("2027 hangul jamo", 20, 4, false, "ab\u1100\u1161\u11A8", true)]
    [DataRow("2027 thai", 20, 4, false, "ab\u0E01\u0E33", true)]
    [DataRow("plain wide", 20, 4, false, "ab\u6f22", true)]
    [DataRow("vs16 inline", 20, 4, true, "ab\u2764\uFE0F", true)]
    public void ModelRestore_ContinuationsRestored(string shape, int width, int height, bool retroactive, string before, bool continues)
    {
        var (original, replica) = ContinuationPair(width, height, retroactive, before);
        Assert.AreEqual(continues, original.CaptureModelState().Screen[0].Cells[3].Continues, $"fixture: {shape} column 3");
        foreach (var t in new[] { original, replica })
            t.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b[D\u001b]133;A\u0007"));
        var differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, $"{shape}: " + string.Join("; ", differences.Take(3)));
    }

    // Never-written cells keep write sequence 0 in the replica (review round 3, RR3#1): a VS16 arriving in a later chunk
    // widens a glyph at the right edge onto the next row's never-written cells, which then read as one glyph.
    [TestMethod]
    [DataRow("vs16 in the next chunk", new[] { "\u2764", "\uFE0F" })]
    [DataRow("vs16 after a style change", new[] { "\u2764\u001b[m\uFE0F" })]
    public void ModelRestore_NeverWrittenCellsStayUnwritten(string shape, string[] after)
    {
        var (original, replica) = ContinuationPair(10, 4, retroactive: true, "\u001b[1;10H");
        Assert.IsNotNull(original.CaptureModelState().Screen[1].Unwritten, "fixture: the next row is never written");
        foreach (var chunk in after.Append("\u001b[D\u001b]133;A\u0007"))
        {
            original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(chunk));
            replica.ApplyRecordedOutput(Encoding.UTF8.GetBytes(chunk));
        }
        var differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, $"{shape}: " + string.Join("; ", differences.Take(3)));
    }

    // A reflow splits a two-cell cluster across a soft wrap (its second half at the next row's first column), and a later
    // reflow rejoins it: the replica must know the halves belong together (review round 4, RR4#1).
    [TestMethod]
    [DataRow("ghostty", "\u0E01\u0E33")]
    [DataRow("kitty", "\u0E01\u0E33")]
    [DataRow("alacritty", "\u1100\u1161\u11A8")]
    [DataRow("wezterm", "\u0915\u093F")]
    [DataRow("foot", "\u0E01\u0E33")]
    [DataRow("vte", "\u1100\u1161\u11A8")]
    [DataRow("windows-terminal", "\u0915\u093F")]
    public void ModelRestore_ClusterSplitAcrossAWrapRejoins(string strategy, string cluster)
    {
        Hex1bTerminal Model() => new(new Hex1bTerminalOptions
        {
            PresentationAdapter = new HeadlessPresentationAdapter(9, 4).WithReflowStrategy(CaseConfiguration.CreateReflowStrategy(strategy)!, enabled: true),
            WorkloadAdapter = new CaseReapplier.DetachedWorkload(),
            Width = 9,
            Height = 4,
            TimeProvider = new FakeTimeProvider(),
            DeferStart = true,
        });
        var original = Model();
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes("abcd" + cluster + "xyz"));
        original.Resize(5, 4);
        var state = original.CaptureModelState();
        Assert.IsTrue(state.Screen[1].Cells[0].Continues, $"fixture: {strategy} did not split the cluster across the wrap");
        var replica = Model();
        replica.RestoreModelState(state);
        foreach (var t in new[] { original, replica })
        {
            t.Resize(9, 4);
            t.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b[1;6H\u001b]133;A\u0007"));
        }
        var differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, $"{strategy}: " + string.Join("; ", differences.Take(3)));
    }

    [TestMethod]
    [DataRow("ghostty")]
    [DataRow("kitty")]
    public void ModelRestore_ClusterSplitAcrossAWrapRejoinsInHistory(string strategy)
    {
        // The split pair scrolls into history before the start; a grow rejoins it there.
        Hex1bTerminal Model() => new(new Hex1bTerminalOptions
        {
            PresentationAdapter = new HeadlessPresentationAdapter(9, 4).WithReflowStrategy(CaseConfiguration.CreateReflowStrategy(strategy)!, enabled: true),
            WorkloadAdapter = new CaseReapplier.DetachedWorkload(),
            Width = 9,
            Height = 4,
            ScrollbackCapacity = 20,
            TimeProvider = new FakeTimeProvider(),
            DeferStart = true,
        });
        var original = Model();
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes("abcd\u0E01\u0E33xyz"));
        original.Resize(5, 4);
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\r\n\r\n\r\n\r\n"));
        var state = original.CaptureModelState();
        Assert.IsTrue(state.History!.Rows.Any(r => r.Cells.Count > 0 && r.Cells[0].Continues), $"fixture: {strategy} has no split cluster in history");
        var replica = Model();
        replica.RestoreModelState(state);
        foreach (var t in new[] { original, replica })
            t.Resize(9, 4);
        var differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, $"{strategy}: " + string.Join("; ", differences.Take(3)));
    }

    // The split pair across the main buffer's boundaries, and across a row boundary that no longer soft-wraps (review
    // round 5: RR5#2, RR5#3). "Flag" is where the start's column-0 continuation must stand, or none.
    [TestMethod]
    [DataRow("history to screen", "", "\u001b[4;1H\n", "", "screen")]
    [DataRow("history to screen under deeper history", "zz\r\n", "\u001b[4;1H\n\n", "", "screen")]
    [DataRow("history to saved main", "", "\u001b[4;1H\n\u001b[?1049h", "\u001b[?1049l", "saved")]
    [DataRow("hard row boundary", "", "\u001b[2;4r\u001b[2;1H\u001bM", "\u001b[4;1H\u001bD\u001b[r", "none")]
    [DataRow("hard boundary at history's end", "", "\u001b[2;4r\u001b[2;1H\u001bM\u001b[4;1H\u001bD\u001b[r\u001b[4;1H\n", "", "none")]
    [DataRow("hard boundary inside history", "", "\u001b[2;4r\u001b[2;1H\u001bM\u001b[4;1H\u001bD\u001b[r\u001b[4;1H\n\n", "", "none")]
    public void ModelRestore_ClusterSplitAcrossABoundary(string shape, string prefix, string before, string after, string flag)
    {
        Hex1bTerminal Model() => new(new Hex1bTerminalOptions
        {
            PresentationAdapter = new HeadlessPresentationAdapter(9, 4).WithReflowStrategy(CaseConfiguration.CreateReflowStrategy("ghostty")!, enabled: true),
            WorkloadAdapter = new CaseReapplier.DetachedWorkload(),
            Width = 9,
            Height = 4,
            ScrollbackCapacity = 10,
            TimeProvider = new FakeTimeProvider(),
            DeferStart = true,
        });
        var original = Model();
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(prefix + "abcd\u0E01\u0E33xyz"));
        original.Resize(5, 4);
        Assert.IsTrue(original.CaptureModelState().Screen.Any(r => r.Cells[0].Continues), $"fixture: {shape} did not split the cluster across the wrap");
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(before));
        var state = original.CaptureModelState();
        var flagged = (state.Screen[0].Cells[0].Continues ? "screen" : "") + (state.SavedMainScreen?[0].Cells[0].Continues == true ? "saved" : "");
        Assert.AreEqual(flag == "none" ? "" : flag, flagged, $"{shape}: the start's column-0 flag at a buffer's first row");
        if (flag == "none")
        {
            Assert.IsFalse(state.Screen.Concat(state.History!.Rows).Any(r => r.Cells[0].Continues), $"{shape}: a column-0 flag after a row that does not soft-wrap");
            Assert.AreEqual(1, state.Screen.Concat(state.History.Rows).Count(r => r.Cells[0].Text.Length == 0), $"fixture: {shape} holds the glyph's second half at a row's start");
        }
        else
        {
            Assert.AreEqual(prefix.Length > 0 ? 2 : 1, state.History!.Rows.Count, $"fixture: {shape} history rows");
        }
        var replica = Model();
        replica.RestoreModelState(state);
        foreach (var step in new[] { after, "RESIZE 9 4", "\u001b[1;6H\u001b]133;A\u0007" })
        {
            ApplyStep(original, step);
            ApplyStep(replica, step);
            var differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
            Assert.IsEmpty(differences, $"{shape} after {JsonSerializer.Serialize(step)}: " + string.Join("; ", differences.Take(3)));
        }
    }

    // The accepted limitation (issue 23, widened in review round 5: RR5#1): a start records continuations between cells
    // adjacent in reading order only. Halves separated before the start (here by an inserted row) and brought back
    // together after it are one glyph in the original and two cells in the replica. This pins the known divergence, so
    // a change to it is noticed; issue 23's fix turns it into a match.
    [TestMethod]
    public void ModelRestore_SeparatedHalvesRejoinedAfterTheStartDiverge()
    {
        Hex1bTerminal Model() => new(new Hex1bTerminalOptions
        {
            PresentationAdapter = new HeadlessPresentationAdapter(9, 4).WithReflowStrategy(CaseConfiguration.CreateReflowStrategy("ghostty")!, enabled: true),
            WorkloadAdapter = new CaseReapplier.DetachedWorkload(),
            Width = 9,
            Height = 4,
            ScrollbackCapacity = 10,
            TimeProvider = new FakeTimeProvider(),
            DeferStart = true,
        });
        var original = Model();
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes("abcd\u0E01\u0E33xyz"));
        original.Resize(5, 4);
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b[2;1H\u001b[L"));
        var replica = Model();
        replica.RestoreModelState(original.CaptureModelState());
        foreach (var step in new[] { "\u001b[2;1H\u001b[M", "RESIZE 9 4", "\u001b[1;6H\u001b]133;A\u0007" })
        {
            ApplyStep(original, step);
            ApplyStep(replica, step);
        }
        Assert.AreEqual((4, 5), (original.CaptureModelState().CommandMarks[0].Column, replica.CaptureModelState().CommandMarks[0].Column),
            "the known one-column divergence of issue 23");
    }

    // The projected never-written runs are exactly the cells with write sequence 0, read raw from the screen and the
    // history rows (review round 4, RR4#4): several runs in a row, and history rows too.
    [TestMethod]
    public void Projection_UnwrittenRunsAreTheSequenceZeroCells()
    {
        var original = MarkModel(MarkScenarios["screen and history"], "none");
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes("abcdefghij\u001b[3G\u001b[2X\u001b[7G\u001b[1X\r\n" + MarkLines(10) + "0123456789\u001b[2G\u001b[3X"));
        var state = original.CaptureModelState();
        var screen = (TerminalCell[,])PrivateField(original, "_screenBuffer");
        for (var row = 0; row < state.Screen.Count; row++)
            CollectionAssert.AreEqual(Zeros(Enumerable.Range(0, screen.GetLength(1)).Select(c => screen[row, c].Sequence)), Mask(state.Screen[row]), $"screen row {row}");
        var history = original.GetScrollbackRows(original.ScrollbackCount).ToList();
        Assert.IsGreaterThan(0, history.Count, "fixture: no history");
        for (var row = 0; row < history.Count; row++)
            CollectionAssert.AreEqual(Zeros(history[row].Cells.ToArray().Select(c => c.Sequence)), Mask(state.History!.Rows[row]), $"history row {row}");
        Assert.IsTrue(state.History!.Rows.Any(r => r.Unwritten is { Count: >= 4 }) || state.Screen.Any(r => r.Unwritten is { Count: >= 4 }),
            "fixture: no row with two runs");

        static bool[] Zeros(IEnumerable<long> sequences) => [.. sequences.Select(s => s == 0)];
        static bool[] Mask(DiagnosticModelRow row)
        {
            var mask = new bool[row.Cells.Count];
            for (var i = 0; row.Unwritten is { } runs && i < runs.Count; i += 2)
                for (var c = runs[i]; c < runs[i] + runs[i + 1]; c++)
                    mask[c] = true;
            return mask;
        }
    }

    // The flag is projected at every column after the first and on history rows too (review round 3, RR3#3).
    [TestMethod]
    public void ModelRestore_ContinuationFlagsProjectedEverywhere()
    {
        var original = MarkModel(MarkScenarios["screen and history"], "none");
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u6f22x\r\n" + MarkLines(12) + "\u6f22y"));
        var state = original.CaptureModelState();
        var history = state.History!.Rows;
        var row = history.Single(r => r.Cells[0].Text == "\u6f22");
        Assert.IsTrue(row.Cells[1].Continues, "a history row's continuation at column 1 is not flagged");
        var replica = MarkModel(MarkScenarios["screen and history"], "none");
        replica.RestoreModelState(state);
        Assert.IsEmpty(JsonDifferences(Json(state), Json(replica.CaptureModelState())), "the flags do not round-trip");
        Assert.IsTrue(state.Screen[state.Cursor.Y].Cells[1].Continues, "a screen row's continuation at column 1 is not flagged");
        // A mark placed on the screen glyph's continuation (column 1) lands on its first cell in both models.
        foreach (var t in new[] { original, replica })
            t.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b[2D\u001b]133;A\u0007"));
        var after = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(after, "a mark on a column-1 continuation: " + string.Join("; ", after.Take(3)));
        Assert.AreEqual(0, original.CaptureModelState().CommandMarks[^1].Column, "fixture: the mark lands on the glyph at column 0");
    }

    [TestMethod]
    [DataRow("wide beside an orphan", "\u001b[1;19H\u6f22\u001b[2;19H\u5b57\u001b[?69h\u001b[1;19s\u001b[1S\u001b[?69l")]
    [DataRow("vs16 cluster beside an orphan", "\u001b[1;19H\u6f22\u001b[2;19Hx\uFE0F\u001b[?69h\u001b[1;19s\u001b[1S\u001b[?69l")]
    public void ModelRestore_OrphanedContinuationsRestored(string shape, string before)
    {
        // A left/right-margin scroll leaves a glyph's second half beside another glyph: not a continuation of it.
        var (original, replica) = ContinuationPair(22, 7, retroactive: false, before);
        foreach (var t in new[] { original, replica })
            t.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b[1;20H\u001b]133;A\u0007"));
        var differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, $"{shape}: " + string.Join("; ", differences.Take(3)));
        Assert.AreEqual(19, original.CaptureModelState().CommandMarks[0].Column, $"fixture: {shape} mark column");
    }

    private static (Hex1bTerminal Original, Hex1bTerminal Replica) ContinuationPair(int width, int height, bool retroactive, string before)
    {
        Hex1bTerminal Model() => new(new Hex1bTerminalOptions
        {
            PresentationAdapter = new HeadlessPresentationAdapter(width, height, retroactive ? TerminalCapabilities.Modern : TerminalCapabilities.Minimal),
            WorkloadAdapter = new CaseReapplier.DetachedWorkload(),
            Width = width,
            Height = height,
            TimeProvider = new FakeTimeProvider(),
            DeferStart = true,
        });
        var original = Model();
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(before));
        var replica = Model();
        replica.RestoreModelState(original.CaptureModelState());
        var differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, "the continuations do not round-trip: " + string.Join("; ", differences.Take(3)));
        return (original, replica);
    }

    [TestMethod]
    [DataRow("a continuation at column 0")]
    [DataRow("a continuation on a glyph")]
    [DataRow("a missing screen row")]
    [DataRow("a screen row without cells")]
    [DataRow("a missing saved main row")]
    [DataRow("a history continuation at column 0")]
    [DataRow("a continuation at column 0 after a row that does not soft-wrap")]
    [DataRow("never-written runs not in pairs")]
    [DataRow("a never-written run past the row")]
    [DataRow("never-written runs out of order")]
    [DataRow("a continuation across a never-written edge")]
    [DataRow("overlapping never-written runs")]
    [DataRow("a never-written run of zero cells")]
    [DataRow("empty never-written runs")]
    [DataRow("a never-written run that overflows")]
    public void ModelRestore_RefusesMalformedRows(string shape)
    {
        // The alternate screen's first row has no row before it in reading order, so nothing there can continue.
        var shapeScenario = shape.Contains("saved") || shape == "a continuation at column 0" ? MarkScenarios["alternate"] : MarkScenarios["screen and history"];
        var original = MarkModel(shapeScenario, "none");
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(shapeScenario.Before + "\u6f22"));
        var state = original.CaptureModelState();
        List<DiagnosticModelRow> Rows(IReadOnlyList<DiagnosticModelRow> rows, int index, Func<DiagnosticModelRow, DiagnosticModelRow> change) =>
            [.. rows.Select((r, j) => j == index ? change(r) : r)];
        DiagnosticModelRow FirstCell(DiagnosticModelRow row, Func<DiagnosticModelCell, DiagnosticModelCell> change) =>
            row with { Cells = [change(row.Cells[0]), .. row.Cells.Skip(1)] };
        var forged = shape switch
        {
            "a continuation at column 0" => state with { Screen = Rows(state.Screen, 0, r => FirstCell(r, c => c with { Text = "", Continues = true })) },
            // Both rows wholly written, so only the missing soft wrap refuses it.
            "a continuation at column 0 after a row that does not soft-wrap" => state with
            {
                Screen = [state.Screen[0] with { Unwritten = null }, FirstCell(state.Screen[1], c => c with { Text = "", Continues = true }) with { Unwritten = null }, .. state.Screen.Skip(2)],
            },
            "a continuation on a glyph" => state with { Screen = Rows(state.Screen, 0, r => r with { Cells = [r.Cells[0], r.Cells[1] with { Text = "x", Continues = true }, .. r.Cells.Skip(2)] }) },
            "a missing screen row" => state with { Screen = Rows(state.Screen, 1, _ => null!) },
            "a screen row without cells" => state with { Screen = Rows(state.Screen, 1, r => r with { Cells = null! }) },
            "a missing saved main row" => state with { SavedMainScreen = Rows(state.SavedMainScreen!, 1, _ => null!) },
            "never-written runs not in pairs" => state with { Screen = Rows(state.Screen, 1, r => r with { Unwritten = [3] }) },
            "a never-written run past the row" => state with { Screen = Rows(state.Screen, 1, r => r with { Unwritten = [30, 11] }) },
            "never-written runs out of order" => state with { Screen = Rows(state.Screen, 1, r => r with { Unwritten = [10, 2, 4, 2] }) },
            "overlapping never-written runs" => state with { Screen = Rows(state.Screen, 1, r => r with { Unwritten = [2, 4, 5, 2] }) },
            "a never-written run of zero cells" => state with { Screen = Rows(state.Screen, 1, r => r with { Unwritten = [2, 0] }) },
            "empty never-written runs" => state with { Screen = Rows(state.Screen, 1, r => r with { Unwritten = [] }) },
            "a never-written run that overflows" => state with { Screen = Rows(state.Screen, 1, r => r with { Unwritten = [1, int.MaxValue] }) },
            "a continuation across a never-written edge" => state with
            {
                Screen = Rows(state.Screen, 1, r => r with
                {
                    Cells = [r.Cells[0] with { Text = "\u6f22" }, r.Cells[1] with { Text = "", Continues = true }, .. r.Cells.Skip(2)],
                    Unwritten = [1, r.Cells.Count - 1],
                }),
            },
            _ => state with { History = state.History! with { Rows = Rows(state.History.Rows, 0, r => FirstCell(r, c => c with { Text = "", Continues = true })) } },
        };
        var replica = MarkModel(shapeScenario, "none");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(forged));
        StringAssert.Contains(error.Message, shape.Contains("continuation") ? "continuation" : shape.Contains("never-written") ? "never-written" : "missing", shape);
        Assert.AreEqual((0L, 0), (replica.CurrentModelSequence, replica.CommandMarks.Count), $"{shape}: a refused restore changed the model");
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(long.MaxValue - 1)]
    public void ModelRestore_AcceptsTheLastAnchorIdBounds(long last)
    {
        // The id bound's accepted edges: the next mark continues from them in both models.
        var original = MarkModel(MarkScenarios["insert delete"], "none");
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes("plain\r\n"));
        var state = original.CaptureModelState() with { LastCommandAnchorId = last };
        var replica = MarkModel(MarkScenarios["insert delete"], "none");
        replica.RestoreModelState(state);
        replica.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b]133;A\u0007$ "));
        Assert.AreEqual($"command:{last + 1}", replica.CaptureModelState().CommandMarks[0].Anchor);
    }

    // Scenarios: the probe's (evidence.md P1), with the strengthened fixtures.
    private sealed record MarkScenario(int Width, int Height, int Capacity, string Before, List<(string Name, string Input)> Steps, string[] Regions);

    private static readonly Dictionary<string, MarkScenario> MarkScenarios = new()
    {
        // Early marks are evicted before the start (gapped ids); the first step scrolls one row.
        ["screen and history"] = new(40, 8, 12, MarkPrompts(1, 3) + MarkLines(12) + MarkPrompts(4, 6) + "\u001b]133;D\u0007",
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
