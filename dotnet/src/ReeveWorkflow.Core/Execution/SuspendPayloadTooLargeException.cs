namespace ReeveWorkflow.Core.Execution;

/// <summary>The state a suspending run wanted to park is bigger than the host will store. It lives here
/// because a caller outside this repository has to be able to catch what the engine catches.</summary>
public sealed class SuspendPayloadTooLargeException(int actualBytes) : Exception(
    $"suspend_payload_too_large: state payload is {actualBytes} bytes; max is 262144 (256 KiB).")
{
    /// <summary>How big the payload actually was. The limit is the host's, so only the host can say by
    /// how much a workflow overran it.</summary>
    public int ActualBytes { get; } = actualBytes;
}
