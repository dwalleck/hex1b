namespace Hex1b.Diagnostics;

/// <summary>
/// An application that publishes immutable frame projections for diagnostics.
/// </summary>
internal interface IApplicationFrameSource
{
    /// <summary>Whether the application publishes a projection after every completed pass.</summary>
    bool FramePublicationEnabled { get; }

    /// <summary>The most recent publication, or <c>null</c> before the first completed pass.</summary>
    PublishedApplicationFrame? LatestFrame { get; }
}
