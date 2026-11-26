using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Execution;

internal sealed class BlockSecrets(
    IWorkflowSecretCodec codec, Guid workflowId, string canvasPath, string nodeId,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stored, ILogger? logger) : IWorkflowBlockSecrets
{
    internal static IReadOnlyList<string> InputsOf(CompiledNode node)
    {
        List<string>? names = null;
        var inputs = node.Shape.Inputs;
        for (var index = 0; index < inputs.Count; index++)
        {
            if (inputs[index].SecretValues)
                (names ??= []).Add(inputs[index].Name);
        }

        return names ?? (IReadOnlyList<string>)[];
    }

    internal static IWorkflowBlockSecrets? For(
        IServiceProvider services, CompiledNode node, CanvasPath canvas, Guid workflowId,
        IReadOnlyDictionary<string, object?> inputs, IReadOnlySet<string> wired, IReadOnlyList<string> secretInputs)
    {
        if (services.GetService<IWorkflowSecretCodec>() is not { } codec)
            return null;
        Dictionary<string, IReadOnlyDictionary<string, string>>? literals = null;
        foreach (var name in secretInputs)
        {
            if (wired.Contains(name) || node.Expressions.ContainsKey(name)
                || !inputs.TryGetValue(name, out var value) || value is not IReadOnlyDictionary<string, string> map)
                continue;
            var copy = new Dictionary<string, string>(map.Count, StringComparer.Ordinal);
            foreach (var (key, text) in map)
                copy[key] = text;

            (literals ??= new(StringComparer.Ordinal))[name] = copy;
        }

        return literals is null
            ? null
            : new BlockSecrets(codec, workflowId, WorkflowSecretSite.CanvasPathText(canvas), node.NodeId, literals,
                services.GetService<ILogger<BlockSecrets>>());
    }

    public async ValueTask<IReadOnlyDictionary<string, string>> RevealAsync(
        string input, IReadOnlyDictionary<string, string> value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(value);
        if (!stored.TryGetValue(input, out var own))
            return value;

        var result = new Dictionary<string, string>(value.Count, StringComparer.Ordinal);
        var revealedAny = false;
        var row = 0;
        foreach (var (key, text) in value)
        {
            row++;
            if (text is null)
                throw new ArgumentException($"Input '{input}' {FieldNames.Naming(key, row)} has no value.");
            if (!own.TryGetValue(key, out var storedText) || !string.Equals(storedText, text, StringComparison.Ordinal))
            {
                result[key] = text;
                continue;
            }

            try
            {
                if (codec.Classify(text) == WorkflowSecretForm.Plain)
                {
                    result[key] = text;
                    continue;
                }

                var site = new WorkflowSecretSite(workflowId, canvasPath, nodeId, input, key);
                result[key] = await codec.RevealAsync(site, text, cancellationToken).ConfigureAwait(false)
                    ?? throw new WorkflowSecretUnresolvedException();
                revealedAny = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var naming = FieldNames.Naming(key, row);
                logger?.LogWarning(
                    "The secret codec failed with {ExceptionType} for workflow {WorkflowId} node {NodeId} input {Input} {Entry}.",
                    exception.GetType().FullName, workflowId, nodeId, input, naming);
                throw new WorkflowSecretUnresolvedException(input, naming);
            }
        }

        return revealedAny ? result : value;
    }
}
