using System.Reflection;
using System.Text;

namespace Hex1b.Tests.Flow;

// Issue 61 (user decision A): Flow's cursor observation first waits up to 250 ms for the terminal's
// output pump to consume a barrier. On a 2-CPU Windows host a large paste starved the thread pool and
// the pump, which ran on the pool, consumed the barrier after 312/383 ms, so Flow suspended history.
// These tests starve the pool on purpose, so they must not run beside other tests.
[TestClass]
[DoNotParallelize]
public class OutputPumpStarvedPoolTests
{
    private static readonly TimeSpan BarrierBound = TimeSpan.FromMilliseconds(250);

    [TestMethod]
    public void Output_IsDeliveredWhileTheThreadPoolIsStarved()
    {
        var presentation = new RecordingPresentation();
        using var workload = new Hex1bAppWorkloadAdapter(presentation);
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithPresentation(presentation)
            .WithDimensions(20, 6)
            .Build();
        workload.Write("warm");
        Assert.IsTrue(presentation.WaitForOutput("warm", TimeSpan.FromSeconds(10)), "The pump must run before starvation.");

        SettlePump();

        using (PoolStarvation.Begin())
        {
            // Written from this (non-pool) thread while every pool thread is blocked.
            workload.Write("starved");
            Assert.IsTrue(presentation.WaitForOutput("starved", BarrierBound),
                "The output pump must keep consuming workload output while the thread pool is starved.");
        }
    }

    [TestMethod]
    public void CursorBarrier_IsConsumedWithinItsBoundWhileTheThreadPoolIsStarved()
    {
        var presentation = new RecordingPresentation();
        using var workload = new Hex1bAppWorkloadAdapter(presentation);
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithPresentation(presentation)
            .WithDimensions(20, 6)
            .Build();
        workload.Write("warm");
        Assert.IsTrue(presentation.WaitForOutput("warm", TimeSpan.FromSeconds(10)), "The pump must run before starvation.");
        SettlePump();

        Task<(int Column, int Row)?> observation;
        TaskCompletionSource<bool> barrier;
        using (PoolStarvation.Begin())
        {
            // The observation enqueues its barrier synchronously on this thread, then waits.
            observation = ((ICursorPositionSource)workload).ObserveCursorPositionAsync(CancellationToken.None);
            barrier = PendingBarrier(workload);
            // Task.Wait completes from the barrier's completion itself, not from a pool continuation.
            Assert.IsTrue(barrier.Task.Wait(BarrierBound),
                "The output pump must consume a cursor barrier within its 250 ms bound while the pool is starved.");
        }

        Assert.IsTrue(observation.Wait(TimeSpan.FromSeconds(10)), "The observation completes once the pool recovers.");
        Assert.AreEqual((3, 2), observation.Result);
    }

    [TestMethod]
    public async Task Dispose_EndsTheOutputPumpThread()
    {
        var presentation = new RecordingPresentation();
        using var workload = new Hex1bAppWorkloadAdapter(presentation);
        var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithPresentation(presentation)
            .WithDimensions(20, 6)
            .Build();
        workload.Write("warm");
        Assert.IsTrue(presentation.WaitForOutput("warm", TimeSpan.FromSeconds(10)));
        var thread = terminal.OutputPumpThreadForTesting;
        Assert.IsNotNull(thread, "The output pump runs on its own thread.");
        Assert.IsTrue(thread.IsAlive && thread.IsBackground);

        await terminal.DisposeAsync();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "Disposal ends the output pump's thread.");
    }

    // Lets the pump finish the warm-up item and wait for the next one, so the starved item is the first
    // it must be resumed for (a pump still inside its warm-up pass would pick it up without resuming).
    private static void SettlePump() => Thread.Sleep(200);

    // The workload adapter keeps each barrier waiting for the pump in a private list; this reads the one
    // the observation just enqueued. It is the measurement point, not part of the verdict's logic.
    private static TaskCompletionSource<bool> PendingBarrier(Hex1bAppWorkloadAdapter workload)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sync = typeof(Hex1bAppWorkloadAdapter).GetField("_barrierSync", flags)!.GetValue(workload)!;
        var pending = (HashSet<TaskCompletionSource<bool>>)typeof(Hex1bAppWorkloadAdapter).GetField("_pendingBarriers", flags)!.GetValue(workload)!;
        lock (sync)
        {
            Assert.HasCount(1, pending, "Exactly one cursor barrier is pending.");
            return pending.Single();
        }
    }

    /// <summary>Blocks every thread-pool worker (and the ones the pool injects) until disposed.</summary>
    private sealed class PoolStarvation : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly CountdownEvent _finished;

        private PoolStarvation(int blockers)
        {
            _finished = new CountdownEvent(blockers);
            for (var i = 0; i < blockers; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static state =>
                {
                    var self = (PoolStarvation)state!;
                    self._release.Wait();
                    self._finished.Signal();
                }, this);
            }
        }

        public static PoolStarvation Begin()
        {
            // Enough blocked items that every current worker takes one and work stays queued for the
            // threads the pool injects while it is starved.
            // The pool creates threads without delay up to its minimum, so starvation holds only once
            // that many workers are blocked and blocked items are still queued.
            ThreadPool.GetMinThreads(out var minimum, out _);
            var starvation = new PoolStarvation(Math.Max(ThreadPool.ThreadCount, minimum) + 64);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (ThreadPool.ThreadCount < minimum || ThreadPool.PendingWorkItemCount < 16)
            {
                if (DateTime.UtcNow > deadline)
                {
                    starvation.Dispose();
                    Assert.Fail("The thread pool could not be starved for the test.");
                }
                Thread.Sleep(5);
            }
            return starvation;
        }

        public void Dispose()
        {
            _release.Set();
            _finished.Wait(TimeSpan.FromSeconds(30));
            _release.Dispose();
            _finished.Dispose();
        }
    }

    /// <summary>A native-shaped presentation that records output and reports the cursor at (3, 2).</summary>
    private sealed class RecordingPresentation : IHex1bTerminalPresentationAdapter, ICursorPositionSource
    {
        private readonly object _sync = new();
        private readonly StringBuilder _output = new();


        public bool WaitForOutput(string text, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            lock (_sync)
            {
                while (!_output.ToString().Contains(text, StringComparison.Ordinal))
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(_sync, remaining))
                        return _output.ToString().Contains(text, StringComparison.Ordinal);
                }
                return true;
            }
        }

        public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            lock (_sync)
            {
                _output.Append(Encoding.UTF8.GetString(data.Span));
                Monitor.PulseAll(_sync);
            }
            return ValueTask.CompletedTask;
        }

        public Task<(int Column, int Row)?> ObserveCursorPositionAsync(CancellationToken cancellationToken)
            => Task.FromResult<(int Column, int Row)?>((3, 2));

        public async ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return ReadOnlyMemory<byte>.Empty;
        }

        public int Width => 20;

        public int Height => 6;

        public TerminalCapabilities Capabilities => TerminalCapabilities.Modern;

        public event Action<int, int>? Resized;

        public event Action? Disconnected;

        public void RaiseEvents()
        {
            Resized?.Invoke(Width, Height);
            Disconnected?.Invoke();
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask EnterRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask ExitRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public (int Row, int Column) GetCursorPosition() => (0, 0);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
