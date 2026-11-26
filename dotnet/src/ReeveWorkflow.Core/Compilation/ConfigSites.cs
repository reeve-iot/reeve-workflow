using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

internal sealed class ConfigSites
{
    private readonly WorkflowNodeDefinition node;
    private readonly HashSet<string> inputs;
    private readonly HashSet<string> fed;
    private readonly bool boundary;

    private ConfigSites(WorkflowNodeDefinition node, HashSet<string> inputs, HashSet<string> fed, bool boundary, bool graded)
    {
        this.node = node;
        this.inputs = inputs;
        this.fed = fed;
        this.boundary = boundary;
        Graded = graded;
    }

    internal bool Graded { get; }

    internal static ConfigSites Of(CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, CompilationContext context)
    {
        var graded = WorkflowCompiler.IsSigned(frame.Path, context) && context.Order.ContainsKey(frame.Path)
            && !context.Ambiguous.Contains(frame.Path);
        if (BoundaryBlockShapes.IsBoundary(node.BlockTypeId))
            return new ConfigSites(node, [], [], boundary: true, graded: false);

        var inputs = new HashSet<string>(shape.Inputs.Select(p => p.Name), StringComparer.Ordinal);
        if (shape.Flags.HasDynamicPorts)
            inputs.UnionWith(StoredWorkflowDocument.PortList(node, StoredDocumentSchema.InputPortsKey).Select(e => e.PortName));
        var fed = new HashSet<string>(StringComparer.Ordinal);
        if (EffectivePorts.TrustworthyResolution(frame, node, shape, context.Environment, context.NodesById(frame.Canvas)) is { } resolved)
        {
            foreach (var name in resolved.Inputs.Select(p => p.Name))
                (shape.ConfigurationFields.Any(f => string.Equals(f.Name, name, StringComparison.Ordinal)) ? fed : inputs).Add(name);
        }

        foreach (var name in node.Passthrough ?? [])
        {
            if (!inputs.Contains(name) && !fed.Contains(name) && EffectivePorts.FindFeedingWire(frame.Canvas, node.Id, name) is { Mapping.IsLiteral: false })
                inputs.Add(name);
        }

        fed.UnionWith(inputs);
        return new ConfigSites(node, inputs, fed, boundary: false, graded);
    }

    internal bool IsSite(string key) => inputs.Contains(key) && WorkflowCompiler.MergeWouldForwardConfigKey(node, key);

    internal bool GradedAsSite(string key) => Graded && IsSite(key);

    internal bool Feeds(string key) =>
        (boundary || fed.Contains(key)) && WorkflowCompiler.MergeWouldForwardConfigKey(node, key) && WorkflowCompiler.IsConfigValueFilled(node, key);
}
