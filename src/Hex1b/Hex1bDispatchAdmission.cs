namespace Hex1b;

/// <summary>Whether an application dispatch operation was admitted.</summary>
public enum Hex1bDispatchAdmission
{
    /// <summary>The callback has an observable completion.</summary>
    Accepted,
    /// <summary>The pending queue is full; the callback was not admitted.</summary>
    QueueFull,
    /// <summary>The application is not running or is stopping; the callback was not admitted.</summary>
    NotRunning
}
