using System.Text;
using Hex1b.Diagnostics;
using Hex1b.Input;

namespace Hex1b.Tests;

[TestClass]
public sealed class Hex1bAppWorkloadAdapterUnicodeInputTests
{
    [TestMethod]
    public async Task RawTextPreservesScalarEventsAndExistingControlMappings()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        await workload.WriteInputAsync(Encoding.UTF8.GetBytes("aA界🚀\t\r\x1b\b "));
        var events = new List<Hex1bKeyEvent>();
        while (workload.InputEvents.TryRead(out var input))
        {
            Assert.IsInstanceOfType<Hex1bKeyEvent>(input);
            events.Add((Hex1bKeyEvent)input);
        }
        CollectionAssert.AreEqual(new[] { "a", "A", "界", "🚀", "\t", "\r", "\x1b", "\b", " " }, events.Select(e => e.Text).ToArray());
        CollectionAssert.AreEqual(new[] { Hex1bKey.A, Hex1bKey.A, Hex1bKey.None, Hex1bKey.None, Hex1bKey.Tab, Hex1bKey.Enter, Hex1bKey.Escape, Hex1bKey.Backspace, Hex1bKey.Spacebar }, events.Select(e => e.Key).ToArray());
        CollectionAssert.AreEqual(new[] { Hex1bModifiers.None, Hex1bModifiers.Shift, Hex1bModifiers.None, Hex1bModifiers.None, Hex1bModifiers.None, Hex1bModifiers.None, Hex1bModifiers.None, Hex1bModifiers.None, Hex1bModifiers.None }, events.Select(e => e.Modifiers).ToArray());
    }

    [TestMethod]
    public async Task BoundedDiagnosticSendTracksOneOrderedAdmissionPerScalar()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithOrderedPasteInput(1).WithHeadless().WithDimensions(20, 3).Build();
        var diagnostics = new TerminalDiagnostics(terminal, "unicode-send");
        var send = diagnostics.TrackSendAsync(() => terminal.SendInputAsync(Encoding.UTF8.GetBytes("🚀界\r"), stop.Token), "text");
        try
        {
            Assert.IsTrue(await workload.InputEvents.WaitToReadAsync(stop.Token));
            Assert.IsFalse(send.IsCompleted, "Capacity one must hold later scalar admissions.");
            Assert.AreEqual(1L, terminal.InputMilestones!.AcceptedInput);
            var events = new List<Hex1bKeyEvent>();
            var ids = new List<long>();
            for (var i = 0; i < 3; i++)
            {
                var input = await workload.InputEvents.ReadAsync(stop.Token);
                Assert.IsInstanceOfType<Hex1bKeyEvent>(input);
                events.Add((Hex1bKeyEvent)input);
                ids.Add(terminal.InputMilestones.IdOf(input)!.Value);
            }
            var receipt = await send.WaitAsync(stop.Token);
            Assert.IsNotNull(receipt);
            CollectionAssert.AreEqual(new[] { "🚀", "界", "\r" }, events.Select(e => e.Text).ToArray());
            CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, ids);
            Assert.AreEqual((1L, 3L), (receipt.FirstId, receipt.LastId));
            Assert.AreEqual(3L, terminal.InputMilestones.AcceptedInput);
            Assert.IsFalse(workload.InputEvents.TryRead(out _), "No surrogate fragments or duplicate admissions remain.");
        }
        finally
        {
            await stop.CancelAsync();
            try { await send; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
}
