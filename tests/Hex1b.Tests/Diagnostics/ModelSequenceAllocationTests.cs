using Hex1b.Tokens;

namespace Hex1b.Tests.Diagnostics;

/// <summary>
/// Advancing the model sequence must not add work that allocates on the unarmed output path.
/// The limits are the per-batch allocations measured at 2728298d, before the sequence existed,
/// so any change that adds allocation to unarmed output application fails here, not only the
/// sequence. Investigate the added allocation before raising a limit.
/// </summary>
[TestClass]
public class ModelSequenceAllocationTests
{
    private const long EmptyBatchBaselineBytes = 32_000;
    private const long OneCharacterBatchBaselineBytes = 266_000;

    [TestMethod]
    public async Task UnarmedApply_AllocationAtBaseline()
    {
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(new Hex1bAppWorkloadAdapter()).WithHeadless().WithDimensions(40, 6).Build();
        IReadOnlyList<AnsiToken> empty = [];
        IReadOnlyList<AnsiToken> text = [new TextToken("x")];
        for (var i = 0; i < 2000; i++)
        {
            terminal.ApplyTokens(empty);
            terminal.ApplyTokens(text);
        }

        var emptyBytes = Measure(() => terminal.ApplyTokens(empty));
        var textBytes = Measure(() => terminal.ApplyTokens(text));

        Assert.IsTrue(emptyBytes <= EmptyBatchBaselineBytes,
            $"empty-batch allocation {emptyBytes} > {EmptyBatchBaselineBytes} per 1000 batches (2728298d baseline); something now allocates on unarmed output application");
        Assert.IsTrue(textBytes <= OneCharacterBatchBaselineBytes,
            $"one-character-batch allocation {textBytes} > {OneCharacterBatchBaselineBytes} per 1000 batches (2728298d baseline); something now allocates on unarmed output application");
    }

    private static long Measure(Action apply)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            apply();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
