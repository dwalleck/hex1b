namespace Hex1b;

/// <summary>What an observed write did.</summary>
internal readonly record struct NativeWriteResult(bool Refused, string? Reason)
{
    public static NativeWriteResult Accepted => new(false, null);

    public static NativeWriteResult Refuse(string reason) => new(true, reason);
}
