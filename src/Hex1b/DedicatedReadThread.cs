using System.Collections.Concurrent;

namespace Hex1b;

/// <summary>
/// Runs blocking reads one at a time on a dedicated background thread, so a read that waits for input
/// does not hold a thread-pool thread (issue 61: on a 2-CPU Windows host the console read held one of
/// the pool's few threads while a large paste starved the rest).
/// </summary>
/// <remarks>
/// Reads keep the outcomes <c>Task.Run(read, ct)</c> gave: a token canceled before the read starts
/// cancels it, a read that throws <see cref="OperationCanceledException"/> for its token is canceled, and
/// any other exception faults it. The awaiting code resumes on the thread pool (one dispatch per read, as
/// before, when the read itself needed one to start); what changes is that a blocked read no longer holds
/// a pool thread. The thread starts with the first read and ends after <see cref="Dispose"/> once the read
/// in progress returns.
/// </remarks>
internal sealed class DedicatedReadThread : IDisposable
{
    private readonly BlockingCollection<Request> _requests = new();
    private readonly string _name;
    private readonly object _startSync = new();
    private Thread? _thread;

    public DedicatedReadThread(string name) => _name = name;

    /// <summary>The thread running the reads, once started (for tests).</summary>
    internal Thread? Thread => Volatile.Read(ref _thread);

    public ValueTask<int> ReadAsync(Func<CancellationToken, int> read, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (ct.IsCancellationRequested)
            return ValueTask.FromCanceled<int>(ct);

        var request = new Request(read, ct, new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously));
        EnsureStarted();
        try
        {
            _requests.Add(request, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            return ValueTask.FromException<int>(new ObjectDisposedException(_name));
        }

        return new ValueTask<int>(request.Completion.Task);
    }

    public void Dispose() => _requests.CompleteAdding();

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _thread) is not null)
            return;
        lock (_startSync)
        {
            if (_thread is not null)
                return;
            var thread = new Thread(Run) { IsBackground = true, Name = _name };
            thread.UnsafeStart();
            Volatile.Write(ref _thread, thread);
        }
    }

    private void Run()
    {
        foreach (var request in _requests.GetConsumingEnumerable())
        {
            if (request.Token.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.Token);
                continue;
            }

            try
            {
                request.Completion.TrySetResult(request.Read(request.Token));
            }
            catch (OperationCanceledException canceled) when (canceled.CancellationToken == request.Token && request.Token.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.Token);
            }
            catch (Exception error)
            {
                request.Completion.TrySetException(error);
            }
        }
    }

    private sealed record Request(Func<CancellationToken, int> Read, CancellationToken Token, TaskCompletionSource<int> Completion);
}
