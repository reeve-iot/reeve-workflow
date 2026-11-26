using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

internal sealed record UntypedPort(string Name, bool IsRequired);

internal sealed record PortSide(IReadOnlyList<PortShape> Typed, IReadOnlyList<UntypedPort> Untyped)
{
    public bool Names(string name) => Typed.Any(p => p.Name == name) || IsUntyped(name);

    public bool IsUntyped(string name) => Untyped.Any(p => p.Name == name);
}

internal static class EffectivePorts
{
    internal const string ErrorOutputName = "_error";

    internal static string? EffectiveReference(WorkflowNodeDefinition node, string referenceInput) =>
        ReadConfigString(node, referenceInput);

    internal static bool IsReferenceInputMapped(WorkflowDefinition canvas, string nodeId, string referenceInput) =>
        MappingsInto(canvas, nodeId).Any(entry => string.Equals(entry.Mapping.TargetInput, referenceInput, StringComparison.Ordinal));

    internal enum PortsUnresolvedReason
    {
        ReferenceIsMapped,
        NoEffectiveReference,
        NoTrustworthyResolution,
    }

    private readonly record struct NodeResolution
    {
        private NodeResolution(CanvasSignature? signature, PortsUnresolvedReason? reason)
        {
            Signature = signature;
            Reason = reason;
        }

        internal CanvasSignature? Signature { get; }
        internal PortsUnresolvedReason? Reason { get; }

        internal static NodeResolution Trustworthy(CanvasSignature signature) => new(signature, null);
        internal static NodeResolution Unresolved(PortsUnresolvedReason reason) => new(null, reason);
    }

    private static NodeResolution ResolveNode(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, NodePortEnvironment environment,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById)
    {
        if (shape.RunsWorkflowFrom is { } referenceInput)
        {
            if (IsReferenceInputMapped(frame.Canvas, node.Id, referenceInput))
                return NodeResolution.Unresolved(PortsUnresolvedReason.ReferenceIsMapped);

            var effective = EffectiveReference(node, referenceInput);
            if (string.IsNullOrEmpty(effective))
                return NodeResolution.Unresolved(PortsUnresolvedReason.NoEffectiveReference);

            return environment.For(frame.Path, node.Id) is { Found: true, Signature: { } sig } resolution
                && string.Equals(resolution.Reference, effective, StringComparison.Ordinal)
                ? NodeResolution.Trustworthy(sig)
                : NodeResolution.Unresolved(PortsUnresolvedReason.NoTrustworthyResolution);
        }

        return environment.For(frame.Path, node.Id) is { Found: true, Signature: { } noReferenceSig }
            ? NodeResolution.Trustworthy(noReferenceSig)
            : NodeResolution.Unresolved(PortsUnresolvedReason.NoTrustworthyResolution);
    }

    internal static CanvasSignature? TrustworthyResolution(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, NodePortEnvironment environment,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById) =>
        shape.Flags.PortsAreResolvedElsewhere ? ResolveNode(frame, node, shape, environment, nodesById).Signature : null;

    internal static bool IsPortsUnresolved(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, NodePortEnvironment environment,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById) =>
        shape.Flags.PortsAreResolvedElsewhere && ResolveNode(frame, node, shape, environment, nodesById).Reason is not null;

    internal static PortsUnresolvedReason? UnresolvedReason(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, NodePortEnvironment environment,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById) =>
        shape.Flags.PortsAreResolvedElsewhere ? ResolveNode(frame, node, shape, environment, nodesById).Reason : null;

    internal static IEnumerable<(string SourceNodeId, WorkflowMappingDefinition Mapping)> MappingsInto(
        WorkflowDefinition canvas, string nodeId)
    {
        foreach (var connection in canvas.GetConnections() ?? [])
        {
            if (connection.TargetNodeId != nodeId || connection.Mappings is null)
                continue;

            foreach (var mapping in WorkflowGraphHelpers.ReadMappings(connection.Mappings.Value))
                yield return (connection.SourceNodeId, mapping);
        }
    }

    internal static (string SourceNodeId, WorkflowMappingDefinition Mapping)? FindFeedingWire(
        WorkflowDefinition canvas, string nodeId, string targetInput)
    {
        (string SourceNodeId, WorkflowMappingDefinition Mapping)? last = null;
        foreach (var entry in MappingsInto(canvas, nodeId))
        {
            if (entry.Mapping.TargetInput == targetInput)
                last = entry;
        }

        return last;
    }

    internal static string? ReadConfigString(WorkflowNodeDefinition node, string key) =>
        node.Config is not null
        && node.Config.TryGetValue(key, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
