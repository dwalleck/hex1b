namespace Hex1b;

/// <summary>
/// How a geometry-gated native delivery ended.
/// </summary>
public enum NativeDeliveryOutcome
{
    /// <summary>The bytes reached the native presentation's write path.</summary>
    Applied,

    /// <summary>
    /// The native presentation no longer reported the geometry the bytes were
    /// composed for, so nothing was written.
    /// </summary>
    GeometryChanged,
}
