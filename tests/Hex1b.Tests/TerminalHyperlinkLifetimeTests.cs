using Hex1b.Reflow;
using Hex1b.Tokens;

namespace Hex1b.Tests;

[TestClass]
public class TerminalHyperlinkLifetimeTests
{
    [TestMethod]
    public void Scroll_RepeatedHyperlinkedLines_PreservesCountsIdentityAndFinalRelease()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless().WithDimensions(40, 10).WithScrollback(12).Build();
        var targets = new List<TrackedObject<HyperlinkData>>();

        for (var line = 0; line < 16; line++)
        {
            Write(terminal, Link($"https://x.test/{line % 3}", $"link {line}") + "\r\n");
            if (line < 3)
                targets.Add(terminal.GetTrackedHyperlinkAt(0, line)!);
            Assert.AreEqual(Math.Min(line + 1, 3), terminal.TrackedHyperlinkCount, $"line {line + 1}");
            AssertOwnership(terminal, targets);
        }

        Assert.AreEqual(7, terminal.ScrollbackCount, "the reported fixture must actually scroll");
        for (var target = 0; target < 3; target++)
        {
            Write(terminal, $"\x1b[10;{target + 1}H" + Link($"https://x.test/{target}", "X"));
            Assert.AreSame(targets[target], terminal.GetTrackedHyperlinkAt(target, 9));
            AssertOwnership(terminal, targets);
        }

        // Retained history keeps targets alive after the screen is erased.
        Write(terminal, "\x1b[2J");
        Assert.AreEqual(3, terminal.TrackedHyperlinkCount);
        AssertOwnership(terminal, targets);
        for (var line = 0; line < 12; line++)
        {
            Write(terminal, "\x1b[10;1H\r\n");
            AssertOwnership(terminal, targets);
        }
        Assert.AreEqual(0, terminal.TrackedHyperlinkCount);
        Assert.IsTrue(targets.All(target => target.RefCount == 0));
    }

    [TestMethod]
    [DataRow("S", false, 1)]
    [DataRow("T", false, 1)]
    [DataRow("L", false, 1)]
    [DataRow("M", false, 1)]
    [DataRow("S", true, 1)]
    [DataRow("T", true, 1)]
    [DataRow("L", true, 1)]
    [DataRow("M", true, 1)]
    [DataRow("S", false, 2)]
    [DataRow("T", false, 2)]
    [DataRow("L", false, 2)]
    [DataRow("M", false, 2)]
    [DataRow("S", true, 2)]
    [DataRow("T", true, 2)]
    [DataRow("L", true, 2)]
    [DataRow("M", true, 2)]
    public void MoveRows_WithinMargins_KeepsExactlyOneReferencePerOwnedCell(
        string command, bool horizontalMargins, int count)
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless().WithDimensions(8, 5).Build();
        var targets = SeedRows(terminal, "ABCDEFGH");
        var upward = command is "S" or "M";
        var sourceRow = upward ? 1 + count : 1;
        var destinationRow = upward ? 1 : 1 + count;
        var moved = terminal.GetTrackedHyperlinkAt(2, sourceRow);

        Write(terminal, "\x1b[2;4r" + (horizontalMargins ? "\x1b[?69h\x1b[3;6s" : "")
            + $"\x1b[2;3H\x1b[{count}{command}");

        Assert.AreSame(moved, terminal.GetTrackedHyperlinkAt(2, destinationRow));
        Assert.AreSame(targets[0], terminal.GetTrackedHyperlinkAt(0, 0));
        Assert.AreSame(targets[4], terminal.GetTrackedHyperlinkAt(7, 4));
        if (horizontalMargins)
        {
            for (var row = 1; row <= 3; row++)
            {
                Assert.AreSame(targets[row], terminal.GetTrackedHyperlinkAt(0, row));
                Assert.AreSame(targets[row], terminal.GetTrackedHyperlinkAt(7, row));
            }
        }
        AssertOwnership(terminal, targets);
        Write(terminal, "\x1b[?69l\x1b[2J");
        AssertOwnership(terminal, targets);
        Assert.AreEqual(0, terminal.TrackedHyperlinkCount);
    }

    [TestMethod]
    [DataRow("ICH", false)]
    [DataRow("DCH", false)]
    [DataRow("DECIC", false)]
    [DataRow("DECDC", false)]
    [DataRow("ICH", true)]
    [DataRow("DCH", true)]
    [DataRow("DECIC", true)]
    [DataRow("DECDC", true)]
    public void MoveColumns_CharactersAndColumns_PreservesWideCellsAndReferences(string command, bool wide)
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless().WithDimensions(10, 3).Build();
        var targets = SeedRows(terminal, wide ? "AB界CDEFGH" : "ABCDEFGHIJ");
        Write(terminal, "\x1b[?69h\x1b[3;9s\x1b[2;3H");
        AnsiToken token = command switch
        {
            "ICH" => new InsertCharacterToken(2),
            "DCH" => new DeleteCharacterToken(2),
            "DECIC" => new InsertColumnsToken(2),
            "DECDC" => new DeleteColumnsToken(2),
            _ => throw new ArgumentException(command)
        };
        terminal.ApplyTokens([token]);

        var inserting = command is "ICH" or "DECIC";
        var movedColumn = inserting ? 4 : 2;
        var expectedCharacter = inserting ? (wide ? "界" : "C") : (wide ? "C" : "E");
        var cells = terminal.GetScreenBuffer();
        Assert.AreEqual(expectedCharacter, cells[1, movedColumn].Character);
        if (command is "DECIC" or "DECDC")
            Assert.AreEqual(expectedCharacter, cells[0, movedColumn].Character);
        else
            Assert.AreEqual(wide ? "界" : "C", cells[0, 2].Character);
        if (inserting && wide)
            Assert.AreEqual("", cells[1, movedColumn + 1].Character, "wide continuation must move too");
        for (var row = 0; row < 3; row++)
        {
            Assert.AreSame(targets[row], cells[row, 0].TrackedHyperlink);
            Assert.AreSame(targets[row], cells[row, 9].TrackedHyperlink);
        }
        AssertOwnership(terminal, targets);
        Write(terminal, "\x1b[?69l\x1b[2J");
        AssertOwnership(terminal, targets);
        Assert.AreEqual(0, terminal.TrackedHyperlinkCount);
    }

    [TestMethod]
    [DataRow("none", 6)]
    [DataRow("crop", 2)]
    [DataRow("reflow", 6)]
    public void AlternateScreen_RepeatEntryAndResize_TransfersMainOwnershipAndReleasesAlternate(
        string resize, int retainedMainCells)
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        var presentation = new HeadlessPresentationAdapter(8, 3);
        if (resize == "reflow")
            presentation.WithReflow(KittyReflowStrategy.Instance);
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithPresentation(presentation).WithDimensions(8, 3).WithScrollback(8).Build();
        Write(terminal, Link("https://main.test", "AB界CD"));
        var main = terminal.GetTrackedHyperlinkAt(0, 0)!;
        Assert.AreEqual(6, main.RefCount);

        terminal.EnterAlternateScreen();
        Write(terminal, "\x1b[1;1H" + Link("https://alternate.test", "Z") + "\x1b[T");
        var alternate = terminal.GetTrackedHyperlinkAt(0, 1)!;
        Assert.AreEqual(6, main.RefCount, "saved main screen owns its references");
        Assert.AreEqual(1, alternate.RefCount, "alternate scroll retains the moved cell");
        Assert.AreEqual(2, terminal.TrackedHyperlinkCount);

        terminal.EnterAlternateScreen();
        Assert.AreEqual(0, alternate.RefCount);
        Assert.AreEqual(6, main.RefCount);
        if (resize != "none")
            terminal.Resize(3, 5);
        terminal.ExitAlternateScreen();

        Assert.AreEqual(retainedMainCells, main.RefCount);
        Assert.AreSame(main, terminal.GetTrackedHyperlinkAt(0, 0));
        AssertOwnership(terminal, [main, alternate]);
        Write(terminal, "\x1b[2J\x1b[3J");
        AssertOwnership(terminal, [main, alternate]);
        Assert.AreEqual(0, terminal.TrackedHyperlinkCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Reflow_HistoryOnlyTarget_PreservesStoreMembershipAndDeduplication(bool thirdParty)
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        ITerminalReflowProvider provider = thirdParty ? new PublicReflowProvider() : KittyReflowStrategy.Instance;
        var presentation = new HeadlessPresentationAdapter(12, 3).WithReflow(provider);
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithPresentation(presentation).WithDimensions(12, 3).WithScrollback(12).Build();
        Write(terminal, Link("https://history.test", "HISTORY"));
        var history = terminal.GetTrackedHyperlinkAt(0, 0)!;
        for (var line = 0; line < 6; line++)
            Write(terminal, "\r\nplain");
        Assert.IsFalse(terminal.GetScreenBuffer().Cast<TerminalCell>().Any(cell => cell.HasHyperlinkData));
        AssertOwnership(terminal, [history]);

        terminal.Resize(10, 3);

        Assert.IsTrue(terminal.GetScrollbackRows(12).Any(row => row.Cells.Any(cell => cell.HasHyperlinkData)),
            "fixture must retain the history-only target through replacement");
        Assert.AreEqual(1, terminal.TrackedHyperlinkCount);
        AssertOwnership(terminal, [history]);
        Write(terminal, "\x1b[3;1H" + Link("https://history.test", "X"));
        Assert.AreSame(history, terminal.GetTrackedHyperlinkAt(0, 2));
        AssertOwnership(terminal, [history]);
        Write(terminal, "\x1b[2J\x1b[3J");
        AssertOwnership(terminal, [history]);
        Assert.AreEqual(0, terminal.TrackedHyperlinkCount);
    }

    [TestMethod]
    public void Reflow_NarrowAndWidenWithCapacityDiscard_CountsScreenAndHistoryOwners()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        var presentation = new HeadlessPresentationAdapter(12, 3).WithReflow(KittyReflowStrategy.Instance);
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithPresentation(presentation).WithDimensions(12, 3).WithScrollback(2).Build();
        var targets = SeedRows(terminal, "ABCDEFGHIJKL");
        terminal.Resize(3, 3);
        Assert.AreEqual(2, terminal.ScrollbackCount, "narrowing must overflow the history capacity");
        Assert.AreEqual(0, targets[0].RefCount, "discarded target must be released");
        AssertOwnership(terminal, targets);
        terminal.Resize(12, 3);
        AssertOwnership(terminal, targets);
        Write(terminal, "\x1b[2J\x1b[3J");
        AssertOwnership(terminal, targets);
        Assert.AreEqual(0, terminal.TrackedHyperlinkCount);
    }

    private static List<TrackedObject<HyperlinkData>> SeedRows(Hex1bTerminal terminal, string text)
    {
        var targets = new List<TrackedObject<HyperlinkData>>();
        for (var row = 0; row < terminal.Height; row++)
        {
            Write(terminal, $"\x1b[{row + 1};1H" + Link($"https://row.test/{row}", text));
            targets.Add(terminal.GetTrackedHyperlinkAt(0, row)!);
        }
        AssertOwnership(terminal, targets);
        return targets;
    }

    private static string Link(string uri, string text) => $"\x1b]8;;{uri}\x1b\\{text}\x1b]8;;\x1b\\";

    private static void Write(Hex1bTerminal terminal, string text) => terminal.ApplyTokens(AnsiTokenizer.Tokenize(text));

    private static void AssertOwnership(Hex1bTerminal terminal, IEnumerable<TrackedObject<HyperlinkData>> knownTargets)
    {
        // Observe actual owners without creating a snapshot (which would add references).
        var cells = terminal.GetScreenBuffer().Cast<TerminalCell>()
            .Concat(terminal.GetScrollbackRows(terminal.ScrollbackCount).SelectMany(row => row.Cells));
        var counts = cells.Where(cell => cell.TrackedHyperlink is not null)
            .GroupBy(cell => cell.TrackedHyperlink!)
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.AreEqual(counts.Count, terminal.TrackedHyperlinkCount, "store must contain exactly the live targets");
        foreach (var target in knownTargets.Concat(counts.Keys).Distinct())
            Assert.AreEqual(counts.GetValueOrDefault(target), target.RefCount, target.Data.Uri);
    }

    // Delegate to the established strategy while exercising the public-provider replacement branch.
    private sealed class PublicReflowProvider : ITerminalReflowProvider
    {
        public bool ShouldClearSoftWrapOnAbsolutePosition => KittyReflowStrategy.Instance.ShouldClearSoftWrapOnAbsolutePosition;
        public ReflowResult Reflow(ReflowContext context) => KittyReflowStrategy.Instance.Reflow(context);
    }
}
