namespace ReeveWorkflow.Abstractions;

/// <summary>Turns the stored form of a secret-capable input's entries into the values to send
/// (<see cref="WorkflowBlockContext.Secrets"/>).</summary>
/// <remarks>A revealed value is plaintext. A block uses it for the call it makes and never writes it to an output, a log
/// line, an exception message or a trace; a failure names the input and the key, never the value.</remarks>
public interface IWorkflowBlockSecrets
{
    /// <summary>The entries of <paramref name="value"/>, in its order, with each protected or referenced entry revealed when the
    /// input was bound from the node's stored literal. An entry that arrived by a wire or an <c>expr:</c> is returned as it
    /// arrived, so run-time data cannot name a reference its author could not bind. With no codec registered, and for a context
    /// built by hand, <paramref name="value"/> is returned unchanged.</summary>
    /// <param name="input">The name of a <see cref="BlockPortDefinition.SecretValues"/> input of the node.</param>
    /// <param name="value">The map the block was handed for that input.</param>
    /// <param name="cancellationToken">Cancels the reveal.</param>
    /// <exception cref="IWorkflowCodedException">The host could not reveal an entry. Its code is
    /// <see cref="WorkflowErrorCodes.SecretUnresolved"/>, so the node fails with it; the message names the input and the key.</exception>
    ValueTask<IReadOnlyDictionary<string, string>> RevealAsync(
        string input, IReadOnlyDictionary<string, string> value, CancellationToken cancellationToken);
}
