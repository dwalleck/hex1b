namespace Hex1b.Tests;

// Issue 61: the terminal's output pump resumes on its own thread through this context.
[TestClass]
public class DedicatedThreadSynchronizationContextTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task Start_ResumesCapturedAwaitsOnTheDedicatedThreadAndEndsWithTheLoop()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DedicatedThreadSynchronizationContext? context = null;
        var resumedOn = -1;
        var onPool = true;
        var loop = DedicatedThreadSynchronizationContext.Start("test loop", async c =>
        {
            context = c;
            await gate.Task; // captured context: resumes on the loop's thread
            resumedOn = Environment.CurrentManagedThreadId;
            onPool = Thread.CurrentThread.IsThreadPoolThread;
        });

        await WaitUntil(() => context is not null);
        gate.SetResult();
        await loop.WaitAsync(Limit);
        Assert.AreEqual(context!.Thread.ManagedThreadId, resumedOn);
        Assert.IsFalse(onPool);
        Assert.IsTrue(context.Thread.Join(Limit), "The thread ends with the loop.");
    }

    [TestMethod]
    public async Task SwitchTo_ReturnsToTheThreadAfterAnAwaitThatLeftIt()
    {
        DedicatedThreadSynchronizationContext? context = null;
        var back = false;
        var loop = DedicatedThreadSynchronizationContext.Start("test loop", async c =>
        {
            context = c;
            await Task.Delay(10).ConfigureAwait(false); // now on the pool
            await c.SwitchTo();
            back = c.IsCurrentThread;
        });

        await loop.WaitAsync(Limit);
        Assert.IsTrue(back);
        Assert.IsTrue(context!.Thread.Join(Limit));
    }

    [TestMethod]
    public async Task Start_PropagatesTheLoopsFaultAndPostsAfterTheEndStillRun()
    {
        DedicatedThreadSynchronizationContext? context = null;
        var loop = DedicatedThreadSynchronizationContext.Start("test loop", async c =>
        {
            context = c;
            await Task.Yield();
            throw new InvalidOperationException("pump failed");
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await loop.WaitAsync(Limit));
        Assert.AreEqual("pump failed", error.Message);
        Assert.IsTrue(context!.Thread.Join(Limit));

        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ => late.SetResult(), null);
        await late.Task.WaitAsync(Limit);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(5);
        }
    }
}
