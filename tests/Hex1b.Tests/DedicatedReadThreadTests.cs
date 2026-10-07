namespace Hex1b.Tests;

// Issue 61: the Windows console read runs on a dedicated thread instead of holding a pool thread.
[TestClass]
public class DedicatedReadThreadTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task ReadAsync_RunsReadsInOrderOnOneDedicatedNonPoolThread()
    {
        using var reader = new DedicatedReadThread("test reader");
        var threads = new List<(int Id, bool Pool)>();
        for (var i = 1; i <= 3; i++)
        {
            var value = i;
            Assert.AreEqual(value, await reader.ReadAsync(_ =>
            {
                threads.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread));
                return value;
            }, TestContext.Current.CancellationToken).AsTask().WaitAsync(Limit));
        }

        Assert.HasCount(3, threads);
        Assert.IsTrue(threads.All(t => t.Id == reader.Thread!.ManagedThreadId && !t.Pool));
        Assert.IsTrue(reader.Thread!.IsBackground, "A blocked read must never keep the process alive.");
    }

    [TestMethod]
    public async Task ReadAsync_KeepsTaskRunCancellationAndFaultOutcomes()
    {
        using var reader = new DedicatedReadThread("test reader");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var ran = false;
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await reader.ReadAsync(_ => { ran = true; return 1; }, canceled.Token));
        Assert.IsFalse(ran, "A read whose token is already canceled never starts.");

        using var during = new CancellationTokenSource();
        var duringRead = reader.ReadAsync(token =>
        {
            during.Cancel();
            token.ThrowIfCancellationRequested();
            return 1;
        }, during.Token).AsTask();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await duringRead.WaitAsync(Limit));
        Assert.IsTrue(duringRead.IsCanceled);

        var faulted = reader.ReadAsync(_ => throw new InvalidOperationException("console failed"), CancellationToken.None).AsTask();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await faulted.WaitAsync(Limit));
        Assert.AreEqual("console failed", error.Message);
        Assert.AreEqual(7, await reader.ReadAsync(_ => 7, CancellationToken.None).AsTask().WaitAsync(Limit), "A fault does not stop the thread.");
    }

    [TestMethod]
    public async Task Dispose_EndsTheThreadAfterTheReadInProgressAndRefusesNewReads()
    {
        var reader = new DedicatedReadThread("test reader");
        using var release = new ManualResetEventSlim();
        var blocked = reader.ReadAsync(_ => { release.Wait(); return 5; }, CancellationToken.None).AsTask();
        var thread = reader.Thread!;
        reader.Dispose();
        Assert.IsTrue(thread.IsAlive, "The read in progress keeps running until it returns.");
        release.Set();
        Assert.AreEqual(5, await blocked.WaitAsync(Limit));
        Assert.IsTrue(thread.Join(Limit), "The thread ends once the last read returns.");
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await reader.ReadAsync(_ => 1, CancellationToken.None));
    }
}
