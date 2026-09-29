namespace Hex1b;

/// <summary>
/// What an observed write did. A presentation that swallows a write error (so the terminal never sees
/// it) reports it as <see cref="Failed"/>; one that lets the error propagate throws instead.
/// </summary>
internal readonly record struct NativeWriteResult(bool Refused, string? Reason, string? Error = null)
{
    public static NativeWriteResult Accepted => new(false, null);

    public bool Failed => Error is not null;

    public static NativeWriteResult Refuse(string reason) => new(true, reason);

    public static NativeWriteResult Fail(Exception error) => new(false, null, $"{error.GetType().FullName}: {error.Message}");
}
