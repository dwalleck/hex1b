namespace Hex1b;

/// <summary>
/// Bytes a write handed to the host before it completed or failed. A presentation that cannot see
/// partial progress leaves <see cref="Observed"/> false, and a failure's accepted bytes are then unknown.
/// </summary>
internal sealed class NativeWriteProgress
{
    public int BytesAccepted { get; private set; }

    public bool Observed { get; private set; }

    public void Advance(int bytes)
    {
        Observed = true;
        BytesAccepted += bytes;
    }
}
