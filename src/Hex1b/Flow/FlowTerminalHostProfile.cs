namespace Hex1b.Flow;

/// <summary>
/// Host identity established by a real presentation capability probe.
/// </summary>
/// <remarks>
/// <para>
/// The value remains <see cref="Unknown"/> until the presentation has completed
/// its startup probe. Unknown native hosts cannot commit history; they never
/// receive Ghostty's OSC 133 semantics based on environment variables or a
/// guessed terminal name. Headless/custom adapters without a provider do not
/// use this gate.
/// </para>
/// <para>
/// The Ghostty value is intentionally pinned to the version whose native
/// reflow behavior this prototype qualified. A future host version must be
/// qualified separately before it is allowed to emit these marks.
/// </para>
/// </remarks>
internal enum FlowTerminalHostProfile
{
    Unknown = 0,
    Ghostty_1_3_1 = 1,
    Ghostty_Unqualified = 2,
    WindowsConsole = 3,
}

