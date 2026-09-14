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

/// <summary>
/// Lazy, presentation-owned Flow host capability source.
/// </summary>
/// <remarks>
/// The source is read by the Flow runner at emission time rather than copied
/// while the builder creates its workload factory. This keeps the profile tied
/// to the result of the presentation adapter's existing startup probe.
/// </remarks>
internal interface IFlowTerminalHostProfileSource
{
    FlowTerminalHostProfile FlowHostProfile { get; }
}

/// <summary>
/// Provides current native terminal dimensions to Flow's serialized write path.
/// </summary>
/// <remarks>
/// A workload adapter normally exposes dimensions delivered by resize events.
/// The native presentation can have applied a resize before that event reaches
/// the adapter, so the final emission check uses this source when available.
/// </remarks>
internal interface IFlowCurrentGeometrySource
{
    (int Width, int Height) ReadCurrentGeometry();
}
