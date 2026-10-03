namespace Hex1b.Diagnostics;

/// <summary>Checkpoint profiles a case can declare.</summary>
public static class DiagnosticCaseCheckpointProfiles
{
    /// <summary>
    /// A model that has applied no output batch and no geometry change, and whose pump has read no
    /// bytes, since construction. Its state is fully determined by its configuration.
    /// </summary>
    public const string FreshModel = "fresh-model/1";

    /// <summary>
    /// A typed projection of the model's full text state, including buffer-cell write equality (<see cref="DiagnosticModelState"/>).
    /// </summary>
    public const string TextState = "text-state/2";
}
