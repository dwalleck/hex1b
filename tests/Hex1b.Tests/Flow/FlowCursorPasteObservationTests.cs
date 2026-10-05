using System.Text;
using System.Reflection;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Flow;

[TestClass]
public class FlowCursorPasteObservationTests
{
    [TestMethod]
    public async Task ObserveCursorPositionAsync_CallerTimeoutAfterYield_DrainsOldReplyBeforeNextQuery()
    {
        var clock = new FakeTimeProvider();
        using var driver = new ScriptedConsoleDriver { Clock = clock, CursorQueryWriteDuration = TimeSpan.FromMilliseconds(200) };
        await using var adapter = new ConsolePresentationAdapter(driver, timeProvider: clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        var source = (ICursorPositionSource)adapter;
        var first = source.ObserveCursorPositionAsync(stop.Token);
        var prefix = adapter.ReadInputAsync(stop.Token).AsTask();
        await driver.CursorQueryFlushed.Task.WaitAsync(stop.Token);
        await driver.CursorReplyReadEntered.Task.WaitAsync(stop.Token);
        for (var i = 0; i < 64; i++) driver.Enqueue(new string('x', 64));
        Assert.AreEqual(new string('x', 4096), Encoding.UTF8.GetString((await prefix.WaitAsync(stop.Token)).Span));
        var second = source.ObserveCursorPositionAsync(stop.Token);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.IsNull(await first.WaitAsync(stop.Token));
        var resumed = adapter.ReadInputAsync(stop.Token).AsTask();
        driver.Enqueue("\x1b[7;1R"); // Late for the caller, inside the first reply window.
        await driver.SecondCursorQueryFlushed.Task.WaitAsync(stop.Token);
        driver.Enqueue("\x1b[20;1Rz");
        var position = await second.WaitAsync(stop.Token);
        Assert.AreEqual(19, position.GetValueOrDefault().Row, "The second query must not accept the first query's late report.");
        Assert.IsTrue(position.HasValue);
        Assert.AreEqual("z", Encoding.UTF8.GetString((await resumed.WaitAsync(stop.Token)).Span));
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_CancelledReaderAfterYield_RetiresAtDeadlineAndPreservesSuffix()
    {
        var clock = new FakeTimeProvider();
        using var driver = new ScriptedConsoleDriver { Clock = clock };
        await using var adapter = new ConsolePresentationAdapter(driver, timeProvider: clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        var source = (ICursorPositionSource)adapter;
        var first = source.ObserveCursorPositionAsync(stop.Token);
        var prefix = adapter.ReadInputAsync(stop.Token).AsTask();
        await driver.CursorQueryFlushed.Task.WaitAsync(stop.Token);
        for (var i = 0; i < 63; i++) driver.Enqueue(new string('x', 64));
        driver.Enqueue(new string('x', 62) + "\x1b[");
        Assert.AreEqual(new string('x', 4094), Encoding.UTF8.GetString((await prefix.WaitAsync(stop.Token)).Span));
        using var cancelledRead = new CancellationTokenSource();
        await cancelledRead.CancelAsync();
        Assert.IsTrue((await adapter.ReadInputAsync(cancelledRead.Token)).IsEmpty);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.IsNull(await first.WaitAsync(stop.Token));
        // No restarted reader: the second request's watchdog must still start,
        // proving the expired yielded query independently released its gate.
        var second = source.ObserveCursorPositionAsync(stop.Token);
        // Synchronize virtual time with asynchronous idle-window retirement;
        // the field only supplies a clock-start barrier, not the verdict.
        var pending = typeof(ConsolePresentationAdapter).GetField("_pendingCursorObservation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        while (pending.GetValue(adapter) is null) await Task.Delay(1, stop.Token);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.IsNull(await second.WaitAsync(stop.Token));
        Assert.AreEqual("\x1b[", Encoding.UTF8.GetString((await adapter.ReadInputAsync(stop.Token)).Span));
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_DisposeAfterYield_CompletesObservationAndStopsQuerying()
    {
        var clock = new FakeTimeProvider();
        using var driver = new ScriptedConsoleDriver { Clock = clock };
        await using var adapter = new ConsolePresentationAdapter(driver, timeProvider: clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        var source = (ICursorPositionSource)adapter;
        var observation = source.ObserveCursorPositionAsync(stop.Token);
        var prefix = adapter.ReadInputAsync(stop.Token).AsTask();
        await driver.CursorQueryFlushed.Task.WaitAsync(stop.Token);
        for (var i = 0; i < 64; i++) driver.Enqueue(new string('x', 64));
        Assert.AreEqual(4096, (await prefix.WaitAsync(stop.Token)).Length);
        var written = driver.WrittenText;
        await adapter.DisposeAsync();
        Assert.IsNull(await observation.WaitAsync(stop.Token));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsNull(await source.ObserveCursorPositionAsync(stop.Token));
        Assert.IsTrue((await adapter.ReadInputAsync(stop.Token)).IsEmpty);
        Assert.AreEqual(written, driver.WrittenText);
    }

    [TestMethod]
    [DataRow(4096)]
    [DataRow(4097)]
    [DataRow(102400)]
    public async Task ObserveCursorPositionAsync_LargeInputBeforeReport_PreservesInputAndAcceptsReport(int inputBytes)
    {
        var clock = new FakeTimeProvider();
        using var driver = new ScriptedConsoleDriver { Clock = clock };
        await using var adapter = new ConsolePresentationAdapter(driver, timeProvider: clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        var source = (ICursorPositionSource)adapter;
        var received = new StringBuilder();
        var inputComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = source.ObserveCursorPositionAsync(stop.Token);
        var reader = Task.Run(async () =>
        {
            while (received.Length < inputBytes || !observation.IsCompleted)
            {
                received.Append(Encoding.UTF8.GetString((await adapter.ReadInputAsync(stop.Token)).Span));
                if (received.Length >= inputBytes) inputComplete.TrySetResult();
            }
        }, stop.Token);
        await driver.CursorQueryFlushed.Task.WaitAsync(stop.Token);
        await driver.CursorReplyReadEntered.Task.WaitAsync(stop.Token);

        // Fixed read-sized chunks preserve FIFO in the existing scripted driver.
        // Frozen virtual time discriminates the byte cap from either deadline.
        var input = new string('x', inputBytes);
        for (var offset = 0; offset < input.Length; offset += 64)
            driver.Enqueue(input.Substring(offset, Math.Min(64, input.Length - offset)));
        driver.Enqueue("\x1b[20;1R");

        var position = await observation.WaitAsync(stop.Token);
        Assert.IsTrue(position.HasValue, "A queued report must remain observable after100KiB input within the reply window.");
        Assert.AreEqual(19, position.GetValueOrDefault().Row);
        Assert.AreEqual(0, position.GetValueOrDefault().Column);
        await inputComplete.Task.WaitAsync(stop.Token);
        await stop.CancelAsync();
        await reader.WaitAsync(TestContext.Current.CancellationToken);
        Assert.AreEqual(input, received.ToString(), "Preserve ordinary input byte-for-byte and exclude only the accepted report.");
    }
}
