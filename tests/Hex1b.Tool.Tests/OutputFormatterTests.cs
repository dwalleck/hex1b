using Hex1b.Tool.Infrastructure;

namespace Hex1b.Tool.Tests;

[DoNotParallelize]
[TestClass]
public class OutputFormatterTests
{
    [TestMethod]
    public void WriteTable_CalculatesColumnWidths()
    {
        var formatter = new OutputFormatter();
        var output = CaptureConsoleOutput(() =>
        {
            formatter.WriteTable(
                ["ID", "NAME", "STATUS"],
                [
                    ["1", "short", "ok"],
                    ["2", "a-longer-name", "running"]
                ]);
        });

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(3, lines.Length); // header + 2 rows

        // Header columns should be padded to widest value
        Assert.Contains("NAME", lines[0]);
        Assert.Contains("a-longer-name", lines[2]);

        // Values should be aligned with header
        var nameStartInHeader = lines[0].IndexOf("NAME", StringComparison.Ordinal);
        var nameStartInRow = lines[2].IndexOf("a-longer-name", StringComparison.Ordinal);
        Assert.AreEqual(nameStartInHeader, nameStartInRow);
    }

    [TestMethod]
    public void WriteTable_JsonMode_ProducesNoOutput()
    {
        var formatter = new OutputFormatter { JsonMode = true };
        var output = CaptureConsoleOutput(() =>
        {
            formatter.WriteTable(
                ["ID"],
                [["1"]]);
        });

        Assert.IsEmpty(output.Trim());
    }

    [TestMethod]
    public void WriteJson_ProducesFormattedJson()
    {
        var formatter = new OutputFormatter { JsonMode = true };
        var output = CaptureConsoleOutput(() =>
        {
            formatter.WriteJson(new { name = "test", count = 42 });
        });

        Assert.Contains("\"name\": \"test\"", output);
        Assert.Contains("\"count\": 42", output);
    }

    [TestMethod]
    public void WriteLine_NormalMode_WritesOutput()
    {
        var formatter = new OutputFormatter();
        var output = CaptureConsoleOutput(() =>
        {
            formatter.WriteLine("hello world");
        });

        Assert.AreEqual("hello world", output.Trim());
    }

    [TestMethod]
    public void WriteLine_JsonMode_SuppressesOutput()
    {
        var formatter = new OutputFormatter { JsonMode = true };
        var output = CaptureConsoleOutput(() =>
        {
            formatter.WriteLine("hello world");
        });

        Assert.IsEmpty(output.Trim());
    }

    [TestMethod]
    public void WriteError_AlwaysWritesToStdErr()
    {
        var formatter = new OutputFormatter { JsonMode = true };
        var errorOutput = CaptureConsoleError(() =>
        {
            formatter.WriteError("something went wrong");
        });

        Assert.AreEqual("something went wrong", errorOutput.Trim());
    }

    [TestMethod]
    public void WriteTable_EmptyRows_WritesHeaderOnly()
    {
        var formatter = new OutputFormatter();
        var output = CaptureConsoleOutput(() =>
        {
            formatter.WriteTable(["COL1", "COL2"], []);
        });

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(1, lines);
        Assert.Contains("COL1", lines[0]);
    }

    [TestMethod]
    public void WriteTable_ColumnsAligned_WithTwoSpaceSeparator()
    {
        var formatter = new OutputFormatter();
        var output = CaptureConsoleOutput(() =>
        {
            formatter.WriteTable(
                ["A", "B"],
                [["xx", "yy"]]);
        });

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // "A " + "  " + "B " pattern — two-space separator
        Assert.Contains("  ", lines[0]);
    }

    [TestMethod]
    public void CaseResult_UnsupportedStartListsItsSurfacesOnce()
    {
        // A start's reason repeats its unsupported surfaces; the text lists them once.
        var formatter = new OutputFormatter();
        var result = new Hex1b.Diagnostics.DiagnosticCaseResult
        {
            Outcome = Hex1b.Diagnostics.DiagnosticOutcome.Captured,
            CaseId = "c",
            Path = "/p",
            State = Hex1b.Diagnostics.DiagnosticCaseState.Recording,
            Checkpoint = new Hex1b.Diagnostics.DiagnosticCaseCheckpoint
            {
                Profile = "text-state/2",
                Status = Hex1b.Diagnostics.DiagnosticCaseCheckpointStatus.Unsupported,
                Reason = "unsupported-surfaces: the start held retained-history, titles, which this checkpoint cannot restore yet.",
                ModelSequence = 5,
                UnsupportedSurfaces = ["retained-history", "titles"],
            },
            Bounds = new Hex1b.Diagnostics.DiagnosticCaseBounds { MaxBytes = 1, MaxSeconds = 1 },
        };
        var output = CaptureConsoleOutput(() => Hex1b.Tool.Commands.Capture.CaseCommandOutput.Write(formatter, result, json: false, "started"));
        var line = output.Split('\n').Single(l => l.StartsWith("Checkpoint:", StringComparison.Ordinal));
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(line, "retained-history").Count, line);
        StringAssert.Contains(line, "text-state/2 unsupported at model sequence 5; unsupported surfaces: retained-history, titles");
    }

    private static string CaptureConsoleOutput(Action action)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static string CaptureConsoleError(Action action)
    {
        var originalErr = Console.Error;
        using var writer = new StringWriter();
        Console.SetError(writer);
        try
        {
            action();
            return writer.ToString();
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}
