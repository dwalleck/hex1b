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
    // widen or move it for a review. One known class can diverge in a wider run and is not a defect of the restore: halves
    // of one glyph separated before the start and brought back together after it (see "Current limitations" in the
    // diagnostic capture guide). The alphabet has no left/right margins, which would make that class common.
    [TestMethod]
    public void ModelRestore_DifferentialFuzz()
    {
        var trials = int.Parse(Environment.GetEnvironmentVariable("HEX1B_RESTORE_FUZZ_TRIALS") ?? "4000");
        var first = int.Parse(Environment.GetEnvironmentVariable("HEX1B_RESTORE_FUZZ_SEED") ?? "1");
        var strategies = CaseConfiguration.StrategyIds.Append("none").ToArray();
        var failures = new List<string>();
        var crossRow = 0;
        for (var seed = first; seed < first + trials; seed++)
        {
            var random = new Random(seed);
            var strategy = strategies[random.Next(strategies.Length)];
            var width = random.Next(4, 12);
            var height = random.Next(2, 7);
            int? scrollback = random.Next(4) switch { 0 => null, 1 => 1, 2 => 2, _ => 10 };
            var modern = random.Next(2) == 0;
            var before = Enumerable.Range(0, random.Next(4, 20)).Select(_ => FuzzStep(random, width, height)).ToList();
            var after = Enumerable.Range(0, random.Next(4, 20)).Select(_ => FuzzStep(random, width, height)).ToList();
            var where = $"seed {seed} ({strategy}, {width}x{height}, scrollback {scrollback?.ToString() ?? "none"}, {(modern ? "modern" : "default")})";

            var original = FuzzModel(width, height, scrollback, strategy, modern);
            foreach (var step in before)
                ApplyStep(original, step);
            var state = original.CaptureModelState();
            if (state.Screen.Concat(state.SavedMainScreen ?? []).Concat(state.History?.Rows ?? []).Any(row => row.Cells.Count > 0 && row.Cells[0].Continues))
                crossRow++;
            var replica = FuzzModel(width, height, scrollback, strategy, modern);
            try
            {
                replica.RestoreModelState(state);
            }
            catch (InvalidOperationException refused)
            {
                failures.Add($"{where}: a real model was refused: {refused.Message} before={JsonSerializer.Serialize(before)}");
                continue;
            }
            var differences = JsonDifferences(Json(state), Json(replica.CaptureModelState()));
            for (var i = 0; differences.Count == 0 && i < after.Count; i++)
            {
                ApplyStep(original, after[i]);
                ApplyStep(replica, after[i]);
                differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
                if (differences.Count > 0)
                    after = after.Take(i + 1).ToList();
            }
            if (differences.Count > 0)
                failures.Add($"{where}: {string.Join("; ", differences.Take(3))} before={JsonSerializer.Serialize(before)} after={JsonSerializer.Serialize(after)}");
        }
        Assert.IsEmpty(failures, string.Join("\n", failures.Take(5)));
        if (trials >= 4000)
            Assert.IsGreaterThan(0, crossRow, "the run never split a glyph across a wrap, so it did not exercise first-column continuations");
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
