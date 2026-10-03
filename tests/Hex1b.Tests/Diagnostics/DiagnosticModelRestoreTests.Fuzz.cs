using System.Text;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticModelRestoreTests
{
    // A differential fuzz of the restore: random output and resizes build a model, a second model is restored from its
    // projection, and the same random steps are then applied to both. The restore must accept every real model, project
    // back to the same state, and stay equal after every later step.
    //
    // The default run is a fixed seed range, so it is deterministic. HEX1B_RESTORE_FUZZ_TRIALS and HEX1B_RESTORE_FUZZ_SEED
    // widen or move it for a review. Separated glyph halves must retain their identity and match when later steps
    // rejoin them, including the cropped-blank shapes at seeds 1046966 and 22719. Named separation and soft-wrap
    // fixtures live in ModelRestore_GlyphHalvesJoinedOnlyAfterTheStartMatch; no seed is exempt from comparison.
    [TestMethod]
    public void ModelRestore_DifferentialFuzz()
    {
        var trials = int.Parse(Environment.GetEnvironmentVariable("HEX1B_RESTORE_FUZZ_TRIALS") ?? "4000");
        var first = int.Parse(Environment.GetEnvironmentVariable("HEX1B_RESTORE_FUZZ_SEED") ?? "1");
        AssertDifferentialFuzz(first, trials);
    }

    // These two crop regressions remain in the ordinary suite, independent of the review seed knobs:
    // Foot's widening/padding shape and WezTerm's DCH-then-narrowing shape exercise distinct histories.
    [TestMethod]
    [DataRow(1046966)]
    [DataRow(22719)]
    public void ModelRestore_CroppedBlankCellsRejoinFromSeed(int seed) => AssertDifferentialFuzz(seed, 1);

    private static void AssertDifferentialFuzz(int first, int trials)
    {
        var strategies = CaseConfiguration.StrategyIds.Append("none").ToArray();
        var failures = new List<string>();
        var crossRow = 0;
        var pendingStarts = 0;
        for (var seed = first; seed < first + trials; seed++)
        {
            var random = new Random(seed);
            var strategy = strategies[random.Next(strategies.Length)];
            var width = random.Next(4, 12);
            var height = random.Next(2, 7);
            int? scrollback = random.Next(4) switch { 0 => null, 1 => 1, 2 => 2, _ => 10 };
            var modern = random.Next(2) == 0;
            // One step list cut at a random point: the start can fall between the two halves of a split chunk, so it
            // holds pending input (ticket 12).
            var all = new List<object>();
            var count = random.Next(8, 40);
            while (all.Count < count)
                all.AddRange(FuzzSteps(random, width, height));
            var cut = random.Next(4, Math.Max(5, all.Count - 3));
            var before = all.Take(cut).ToList();
            var after = all.Skip(cut).ToList();
            var where = $"seed {seed} ({strategy}, {width}x{height}, scrollback {scrollback?.ToString() ?? "none"}, {(modern ? "modern" : "default")})";

            var original = FuzzModel(width, height, scrollback, strategy, modern);
            DiagnosticModelState state;
            try
            {
                foreach (var step in before)
                    ApplyFuzzStep(original, step);
                state = original.CaptureModelState();
            }
            catch (Exception crash)
            {
                failures.Add($"{where}: the model threw {crash.GetType().Name} before the start: {crash.Message} before={Describe(before)}");
                continue;
            }
            if (state.Screen.Concat(state.SavedMainScreen ?? []).Concat(state.History?.Rows ?? []).Any(row => row.Cells.Count > 0 && row.Cells[0].Continues))
                crossRow++;
            var p = state.PendingInput;
            if (p.EscapePrefix.Length > 0 || p.Utf8.Length > 0 || p.GroundEscape || p.FramerUtf8Remaining != 0)
                pendingStarts++;
            var replica = FuzzModel(width, height, scrollback, strategy, modern);
            try
            {
                replica.RestoreModelState(state);
            }
            catch (InvalidOperationException refused)
            {
                failures.Add($"{where}: a real model was refused: {refused.Message} before={Describe(before)}");
                continue;
            }
            var differences = JsonDifferences(Json(state), Json(replica.CaptureModelState()));
            for (var i = 0; differences.Count == 0 && i < after.Count; i++)
            {
                try
                {
                    ApplyFuzzStep(original, after[i]);
                    ApplyFuzzStep(replica, after[i]);
                    differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
                }
                catch (Exception crash)
                {
                    differences = [$"a model threw {crash.GetType().Name}: {crash.Message}"];
                }
                if (differences.Count > 0)
                    after = after.Take(i + 1).ToList();
            }
            if (differences.Count > 0)
                failures.Add($"{where}: {string.Join("; ", differences.Take(3))} before={Describe(before)} after={Describe(after)}");
        }
        Assert.IsEmpty(failures, string.Join("\n", failures.Take(5)));
        if (trials >= 4000)
        {
            Assert.IsGreaterThan(0, crossRow, "the run never split a glyph across a wrap, so it did not exercise first-column continuations");
            Assert.IsGreaterThan(0, pendingStarts, "the run never started between the halves of a split chunk, so it did not exercise pending input");
        }
    }

    private static Hex1bTerminal FuzzModel(int width, int height, int? scrollback, string strategy, bool modern)
    {
        var capabilities = modern ? TerminalCapabilities.Modern : null;
        var presentation = strategy == "none"
            ? new HeadlessPresentationAdapter(width, height, capabilities)
            : new HeadlessPresentationAdapter(width, height, capabilities).WithReflowStrategy(CaseConfiguration.CreateReflowStrategy(strategy)!, enabled: true);
        return new Hex1bTerminal(new Hex1bTerminalOptions
        {
            PresentationAdapter = presentation,
            WorkloadAdapter = new CaseReapplier.DetachedWorkload(),
            Width = width,
            Height = height,
            ScrollbackCapacity = scrollback,
            TimeProvider = new FakeTimeProvider(),
            DeferStart = true,
        });
    }

    private static void ApplyFuzzStep(Hex1bTerminal terminal, object step)
    {
        if (step is byte[] bytes)
            terminal.ApplyRecordedOutput(bytes);
        else
            ApplyStep(terminal, (string)step);
    }

    private static string Describe(IEnumerable<object> steps) =>
        JsonSerializer.Serialize(steps.Select(s => s is byte[] b ? "bytes:" + Convert.ToHexString(b) : s));

    // One random step, or two when a text step is cut into two byte chunks (one time in four): the cut can fall inside
    // a scalar or an escape sequence, and a start between the halves holds that pending input.
    private static IEnumerable<object> FuzzSteps(Random random, int width, int height)
    {
        var step = FuzzStep(random, width, height);
        if (step.StartsWith("RESIZE ", StringComparison.Ordinal) || random.Next(4) != 0)
            return [step];
        var bytes = Encoding.UTF8.GetBytes(step);
        if (bytes.Length < 2)
            return [step];
        var cut = random.Next(1, bytes.Length);
        return [bytes[..cut], bytes[cut..]];
    }

    // One random step: text with a wide or multi-cell cluster (weighted up), a resize (weighted up), row and region
    // edits, scrolls, cursor moves, alternate-screen switches, erases, and command marks.
    private static string FuzzStep(Random random, int width, int height)
    {
        string[] clusters = ["กำ", "각", "\u001b[?2027hकि", "漢", "❤️", "❤\u001b[m️"];
        var kind = random.Next(34);
        if (kind >= 26)
            kind = kind < 30 ? 0 : 3;
        return kind switch
        {
            0 or 1 or 2 => new string('a', random.Next(0, width)) + clusters[random.Next(clusters.Length)] + "xyz",
            3 or 4 => $"RESIZE {random.Next(4, 14)} {random.Next(2, 8)}",
            5 => $"\u001b[{random.Next(1, 3)}L",
            6 => $"\u001b[{random.Next(1, 3)}M",
            7 => $"\u001b[{random.Next(1, 3)};{random.Next(2, height + 1)}r",
            8 => "\u001b[r",
            9 => $"\u001b[{random.Next(1, 3)}S",
            10 => $"\u001b[{random.Next(1, 3)}T",
            11 => "\u001bM",
            12 => "\u001bD",
            13 => $"\u001b[{random.Next(1, height + 1)};{random.Next(1, width + 1)}H",
            14 => "\r\n",
            15 => "\n\n\n",
            16 => "\u001b[?1049h",
            17 => "\u001b[?1049l",
            18 => "\u001b[?47h",
            19 => "\u001b[?47l",
            20 => "\u001b[3J",
            21 => "\u001b]133;A\u0007",
            22 => $"\u001b[{random.Next(1, height + 1)};{random.Next(1, width + 1)}H\u001b]133;B\u0007",
            23 => $"\u001b[{random.Next(3)}J",
            24 => $"\u001b[{random.Next(1, 3)}@",
            _ => $"\u001b[{random.Next(1, 3)}P",
        };
    }
}
