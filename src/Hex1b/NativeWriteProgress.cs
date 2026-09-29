namespace Hex1b;

/// <summary>
/// Bytes a write handed to the host before it completed or failed. A presentation that cannot see
/// partial progress leaves <see cref="Observed"/> false, and a failure's accepted bytes are then unknown.
/// </summary>
internal sealed class NativeWriteProgress
{
    public int BytesAccepted { get; private set; }

    public bool Observed { get; private set; }

    /// <summary>Declares that this write's progress is observed, so zero accepted bytes means zero, not unknown.</summary>
    public void Observe() => Observed = true;

    public void Advance(int bytes)
    {
        Observed = true;
        BytesAccepted += bytes;
    }
}
