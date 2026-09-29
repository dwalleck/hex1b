using Hex1b.Input;

namespace Hex1b.Diagnostics;

/// <summary>
/// Receives input and frame observations as the input milestone tracker records them. Called under
/// the tracker's lock, so an implementation must not block or call back into the tracker.
/// </summary>
internal interface IDiagnosticStreamObserver
{
    void OnInputAccepted(long id, string kind, string source, Hex1bEvent? evt);

    void OnInputProcessed(long id, string applicationInstanceId, long watermark);

    void OnFramePublished(string applicationInstanceId, long frameId, long processedInput, bool wroteOutput, long? outputMark);
}
