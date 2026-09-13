namespace Hex1b.Flow;

/// <summary>
/// Thrown when a history commit cannot be admitted because another commit is
/// already outstanding.
/// </summary>
/// <remarks>
/// Distinct from the framework's other <see cref="InvalidOperationException"/>
/// failures (readiness timeout, missing coordinator, unstable unit count) so a
/// caller can tell "nothing was emitted" apart from "emission state is unknown".
/// A rejected request is never an emission failure and must not suspend history.
/// </remarks>
public sealed class FlowCommitAdmissionException : InvalidOperationException
{
    internal FlowCommitAdmissionException(string message)
        : base(message)
    {
    }
}
