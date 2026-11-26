using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

public static partial class WorkflowCompiler
{
    internal const string ErrorPolicyKey = "errorPolicy";

    private static void CheckErrorPolicy(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, CompilationContext context) =>
        context.Diagnostics.AddRange(ErrorPolicyForms(node, frame.Path.Segments, shape.Source));

    internal static IEnumerable<CompilationDiagnostic> ErrorPolicyForms(
        WorkflowNodeDefinition node, IReadOnlyList<CanvasPathSegment> path, string? blockSource)
    {
        var boundary = BoundaryBlockShapes.IsBoundary(node.BlockTypeId);
        if (node.Config?.ContainsKey(ErrorPolicyKey) == true)
        {
            yield return new CompilationDiagnostic(
                CompilationRules.ErrorPolicyInConfiguration,
                boundary
                    ? $"Node '{node.Id}' keeps an error policy in its configuration, where the run never reads it, and a "
                        + $"{node.BlockTypeId} node has no error policy: remove the configuration key."
                    : $"Node '{node.Id}' keeps its error policy in its configuration, where the run never reads it: move it "
                        + "onto the node as the node's own 'errorPolicy', the one that applies, and remove the configuration key.",
                node.Id, ErrorPolicyKey, path, blockSource);
        }

        if (node.ErrorPolicy is not null && boundary)
        {
            yield return new CompilationDiagnostic(
                CompilationRules.ErrorPolicyOnBoundaryNode,
                $"Node '{node.Id}' is a {node.BlockTypeId} node, which the run skips and which has no error policy: "
                + "remove its 'errorPolicy'.",
                node.Id, ErrorPolicyKey, path, blockSource);
        }

        if (node.ErrorPolicy?.RouteToErrorPort == true)
        {
            yield return new CompilationDiagnostic(
                CompilationRules.ErrorPolicyRouteUnread,
                $"Node '{node.Id}' sets 'routeToErrorPort', which no run reads: remove it. A node offers '_error' "
                + "under the EmitError policy, and under no other.",
                node.Id, ErrorPolicyKey, path, blockSource);
        }
    }
}
