namespace Hex1b.Events;

/// <summary>The identity and cancellation handle for a captured ordered paste.</summary>
public sealed class OrderedPasteStart
{
    private int _cancelled;
    private Action? _wakeOwner;
    private Exception? _error;
    internal Exception? Error => Volatile.Read(ref _error);
    internal void Fail(Exception error) { Interlocked.CompareExchange(ref _error, error, null); Cancel(); }
    internal OrderedPasteStart(Hex1b.Input.OrderedInputState? input = null)
    {
        IsFlowInput = input?.IsFlow == true;
        FlowEpoch = input?.CaptureFlowEpoch();
    }
    internal bool IsFlowInput { get; }
    internal Hex1b.Input.OrderedInputState.FlowEpoch? FlowEpoch { get; }
    internal bool LostFlowOwner => IsFlowInput && (FlowEpoch is null || FlowEpoch.IsClosed);
    /// <summary>Gets this operation's unique identity.</summary>
    public Guid Id { get; } = Guid.NewGuid();
    /// <summary>Gets whether cancellation was requested.</summary>
    public bool IsCancellationRequested => Volatile.Read(ref _cancelled) != 0;
    /// <summary>Requests cancellation without invoking the receiver on the calling thread.</summary>
    /// <remarks>Safe to call from another thread. The input owner delivers the terminal notification
    /// on its next turn. Already applied text remains, and a completed operation is never notified twice.</remarks>
    public void Cancel()
    {
        Interlocked.Exchange(ref _cancelled, 1);
        Volatile.Read(ref _wakeOwner)?.Invoke();
    }

    internal void SetOwnerWake(Action? wake)
    {
        Volatile.Write(ref _wakeOwner, wake);
        if (wake is not null && IsCancellationRequested) wake();
    }
}
