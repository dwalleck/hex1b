using System.Text;
using System.Reflection;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Flow;

[TestClass]
public class FlowCursorPasteObservationTests
{
    // The adapter's read-ahead bound (issue 61): an observation keeps reading for its reply
    // past ordinary input until this much waits undelivered, then yields that input first.
    private const int ReadAheadBytes = 1024 * 1024;

    // Fills an observation's read-ahead so that it yields on the read of lastRead (one read
    // of 64 to 256 bytes): 255 scan windows of 4096 bytes and 63 reads of 64 bytes leave the
    // read-ahead 64 bytes short, so the window lastRead completes crosses the bound.
    private static void FillReadAheadThenYieldOn(ScriptedConsoleDriver driver, string lastRead)
    {
        for (var i = 0; i < ReadAheadBytes / 64 - 1; i++) driver.Enqueue(new string('x', 64));
        driver.Enqueue(lastRead);
    }

    private static async Task<string> ReadInputAsync(ConsolePresentationAdapter adapter, int length, CancellationToken token)
    {
        var received = new StringBuilder();
        while (received.Length < length)
            received.Append(Encoding.UTF8.GetString((await adapter.ReadInputAsync(token)).Span));
        return received.ToString();
    }

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
        FillReadAheadThenYieldOn(driver, new string('x', 64));
        // The yield delivers read-ahead input one read at a time, oldest first.
        Assert.AreEqual(new string('x', 256), Encoding.UTF8.GetString((await prefix.WaitAsync(stop.Token)).Span));
        var second = source.ObserveCursorPositionAsync(stop.Token);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        // The first caller's watchdog expired at t=250 after its query was written, so it
        // no longer decides (framework 53, decision A); the yielded query keeps its window.
        // An early (wrong) completion finishes the async caller on a pooled continuation; let it surface.
        await Task.WhenAny(first, Task.Delay(TimeSpan.FromMilliseconds(200), stop.Token));
        Assert.IsFalse(first.IsCompleted, "The caller watchdog must not decide after the query write began.");
        // The reader keeps going as the terminal's input pump does: it resumes the yielded
        // query, then delivers read-ahead input and admits the second query between deliveries.
        var rest = Task.Run(() => ReadInputAsync(adapter, ReadAheadBytes - 256 + 1, stop.Token), stop.Token);
        driver.Enqueue("\x1b[7;1R"); // After the caller watchdog, inside the first reply window.
        Assert.AreEqual(6, (await first.WaitAsync(stop.Token)).GetValueOrDefault((-1, -1)).Row,
            "The first query's in-window reply is its own result.");
        await driver.SecondCursorQueryFlushed.Task.WaitAsync(stop.Token);
        driver.Enqueue("\x1b[20;1Rz");
        var position = await second.WaitAsync(stop.Token);
        Assert.AreEqual(19, position.GetValueOrDefault().Row, "The second query must not accept the first query's late report.");
        Assert.IsTrue(position.HasValue);
        // Input read before either reply is delivered first, then the input after the second.
        Assert.AreEqual(new string('x', ReadAheadBytes - 256) + "z", await rest.WaitAsync(stop.Token));
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
        // The yield keeps a possible split report ("ESC [") as its retained scan suffix.
        FillReadAheadThenYieldOn(driver, new string('x', 254) + "\x1b[");
        Assert.AreEqual(new string('x', 256), Encoding.UTF8.GetString((await prefix.WaitAsync(stop.Token)).Span));
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
        var yielded = ReadAheadBytes - 64 + 254;
        Assert.AreEqual(new string('x', yielded - 256) + "\x1b[", await ReadInputAsync(adapter, yielded - 256 + 2, stop.Token));
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
        FillReadAheadThenYieldOn(driver, new string('x', 64));
        Assert.AreEqual(256, (await prefix.WaitAsync(stop.Token)).Length);
        var written = driver.WrittenText;
        await adapter.DisposeAsync();
        Assert.IsNull(await observation.WaitAsync(stop.Token));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsNull(await source.ObserveCursorPositionAsync(stop.Token));
        Assert.IsTrue((await adapter.ReadInputAsync(stop.Token)).IsEmpty);
        Assert.AreEqual(written, driver.WrittenText);
    }

    // Issue 61: the terminal answers the cursor query behind a burst it already queued (a
    // 100 KiB paste on Windows), and the application consumes delivered input at its own
    // pace: ordered paste input makes the reader wait for the app. Model that pace on the
    // fake clock, 20 us per delivered byte (about 2 s per 100 KiB; a Linux run measured 2.4 s).
    private static readonly TimeSpan AppTimePerDeliveredByte = TimeSpan.FromTicks(200);

    // A bracketed paste whose lines are numbered, so loss, duplication or reordering shows.
    private static string NumberedPaste(int lines)
    {
        var paste = new StringBuilder("\x1b[200~");
        for (var line = 0; line < lines; line++)
            paste.Append(line.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)).Append(' ').Append('p', 72).Append('\r');
        return paste.Append("\x1b[201~").ToString();
    }

    private static void EnqueueInReads(ScriptedConsoleDriver driver, string input)
    {
        for (var offset = 0; offset < input.Length; offset += 64)
            driver.Enqueue(input.Substring(offset, Math.Min(64, input.Length - offset)));
    }

    [TestMethod]
    [DataRow(200)]  // 20 us per byte, about 2 s per 100 KiB
    [DataRow(1000)] // 100 us per byte: a slower application must not matter either
    public async Task ObserveCursorPositionAsync_ReplyBehindPasteConsumedAtAppPace_ReportsPositionAndKeepsInput(int appTicksPerByte)
    {
        var clock = new FakeTimeProvider();
        using var driver = new ScriptedConsoleDriver { Clock = clock };
        driver.RepliesOnCursorQueryFlush.Enqueue("\x1b[20;1Rk"); // the reply, then a key typed after it
        await using var adapter = new ConsolePresentationAdapter(driver, timeProvider: clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        var paste = NumberedPaste(1280); // 102,400 characters of lines plus the bracket markers
        EnqueueInReads(driver, paste);

        var observation = ((ICursorPositionSource)adapter).ObserveCursorPositionAsync(stop.Token);
        var received = new StringBuilder();
        while (received.Length < paste.Length + 1)
        {
            var data = await adapter.ReadInputAsync(stop.Token);
            received.Append(Encoding.UTF8.GetString(data.Span));
            clock.Advance(TimeSpan.FromTicks(appTicksPerByte) * data.Length);
        }

        var position = await observation.WaitAsync(stop.Token);
        Assert.IsTrue(position.HasValue, "The terminal answered at once; the reply only waited behind queued input.");
        Assert.AreEqual(19, position.GetValueOrDefault().Row);
        Assert.AreEqual(0, position.GetValueOrDefault().Column);
        Assert.AreEqual(paste + "k", received.ToString(),
            "Every pasted byte arrives once, in order, the reply is not input, and later input follows the paste.");
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_NextObservationWhilePasteBacklogIsDelivered_ReportsPosition()
    {
        var clock = new FakeTimeProvider();
        using var driver = new ScriptedConsoleDriver { Clock = clock };
        driver.RepliesOnCursorQueryFlush.Enqueue("\x1b[20;1R");
        driver.RepliesOnCursorQueryFlush.Enqueue("\x1b[21;1R");
        await using var adapter = new ConsolePresentationAdapter(driver, timeProvider: clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        var source = (ICursorPositionSource)adapter;
        var paste = NumberedPaste(1280);
        EnqueueInReads(driver, paste);

        // Flow observes the boundary again for its next unit while the app is still
        // consuming the paste that preceded the first reply.
        var first = source.ObserveCursorPositionAsync(stop.Token);
        var received = new StringBuilder();
        var data = await adapter.ReadInputAsync(stop.Token);
        received.Append(Encoding.UTF8.GetString(data.Span));
        clock.Advance(AppTimePerDeliveredByte * data.Length);
        // The caller's completion runs on a pooled continuation; wait for it in real time.
        Assert.AreEqual(19, (await first.WaitAsync(TimeSpan.FromSeconds(2), stop.Token)).GetValueOrDefault((-1, -1)).Row);
        Assert.IsLessThan(paste.Length / 2, received.Length, "The first reply was found while most of the paste was still undelivered.");

        var second = source.ObserveCursorPositionAsync(stop.Token);
        while (received.Length < paste.Length)
        {
            data = await adapter.ReadInputAsync(stop.Token);
            received.Append(Encoding.UTF8.GetString(data.Span));
            clock.Advance(AppTimePerDeliveredByte * data.Length);
        }

        var position = await second.WaitAsync(stop.Token);
        Assert.IsTrue(position.HasValue, "A query issued during the backlog is admitted and answered within its windows.");
        Assert.AreEqual(20, position.GetValueOrDefault().Row);
        Assert.AreEqual(paste, received.ToString());
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_ReadAheadDelivery_NeverEndsInsideAnEscapeSequence()
    {
        var clock = new FakeTimeProvider();
        using var driver = new ScriptedConsoleDriver { Clock = clock };
        driver.RepliesOnCursorQueryFlush.Enqueue("\x1b[20;1R");
        await using var adapter = new ConsolePresentationAdapter(driver, timeProvider: clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        // Typed keys queued ahead of the reply: text and Up arrows, 6 bytes a period, so
        // fixed 256-byte slices would end inside an arrow's sequence.
        var keys = new StringBuilder();
        while (keys.Length < 8192) keys.Append("abc\x1b[A");
        var input = keys.ToString();
        EnqueueInReads(driver, input);

        var observation = ((ICursorPositionSource)adapter).ObserveCursorPositionAsync(stop.Token);
        var received = new StringBuilder();
        while (received.Length < input.Length)
        {
            var chunk = Encoding.UTF8.GetString((await adapter.ReadInputAsync(stop.Token)).Span);
            if (received.Length + chunk.Length < input.Length)
                Assert.IsFalse(chunk.EndsWith('\x1b') || chunk.EndsWith("\x1b["),
                    $"A delivery ended inside an escape sequence at offset {received.Length + chunk.Length}.");
            received.Append(chunk);
        }

        Assert.AreEqual(19, (await observation.WaitAsync(stop.Token)).GetValueOrDefault((-1, -1)).Row);
        Assert.AreEqual(input, received.ToString());
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
