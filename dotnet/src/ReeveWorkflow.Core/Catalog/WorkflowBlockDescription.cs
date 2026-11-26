using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Catalog;

/// <summary>Flattens a factory into the shape a catalogue reader receives. Here rather than in the host so
/// the endpoint and <c>IWorkflowService</c> cannot describe the same block two different ways.</summary>
public static class WorkflowBlockDescription
{
    public static IReadOnlyCollection<WorkflowBlockTypeResponse> Describe(
        IEnumerable<IWorkflowBlockFactory> factories) =>
        factories.Select(Describe).ToArray();

    public static WorkflowBlockTypeResponse Describe(IWorkflowBlockFactory factory)
    {
        var snapshot = new FactorySnapshot(factory);
        _ = Compilation.BlockShape.From(snapshot);
        return new(snapshot.BlockTypeId,
            snapshot.DisplayName,
            snapshot.Description,
            snapshot.Category,
            Describe(snapshot.Inputs),
            Describe(snapshot.Outputs),
            snapshot.ConfigurationSchema
                .Select(c => new BlockConfigFieldResponse(
                    c.Name, c.Label, c.Type.ToString(), c.Required, c.DefaultValue, c.Description)
                { AllowedValues = c.AllowedValues?.ToArray() })
                .ToArray(),
            snapshot.SubCanvasNames.ToArray(),
            snapshot.SubCanvasNames.ToDictionary(n => n, n => Describe(snapshot.GetSubCanvasInputs(n))),
            snapshot.SubCanvasNames.ToDictionary(n => n, n => Describe(snapshot.GetSubCanvasOutputs(n))),
            snapshot.HasDynamicPorts,
            snapshot.SupportsDeclaredOutputs)
        {
            RunsWorkflowFrom = snapshot.RunsWorkflowFrom,
            TypeParameters = snapshot.TypeParameters
                .Select(p => new BlockTypeParameterResponse(p.Name, p.Constraints.ToArray()))
                .ToArray(),
            DeclaredOutputsCollect = snapshot.DeclaredOutputsCollect,
        };
    }

    private sealed class FactorySnapshot : IWorkflowBlockFactory
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyCollection<BlockPortDefinition>> inputs;
        private readonly IReadOnlyDictionary<string, IReadOnlyCollection<BlockPortDefinition>> outputs;

        public FactorySnapshot(IWorkflowBlockFactory factory)
        {
            BlockTypeId = factory.BlockTypeId;
            DisplayName = factory.DisplayName;
            Description = factory.Description;
            Category = factory.Category;
            Inputs = factory.Inputs.ToArray();
            Outputs = factory.Outputs.ToArray();
            ConfigurationSchema = factory.ConfigurationSchema.ToArray();
            SubCanvasNames = factory.SubCanvasNames.ToArray();
            inputs = SubCanvasNames.Distinct(StringComparer.Ordinal).ToDictionary(name => name, name => (IReadOnlyCollection<BlockPortDefinition>)factory.GetSubCanvasInputs(name).ToArray(), StringComparer.Ordinal);
            outputs = SubCanvasNames.Distinct(StringComparer.Ordinal).ToDictionary(name => name, name => (IReadOnlyCollection<BlockPortDefinition>)factory.GetSubCanvasOutputs(name).ToArray(), StringComparer.Ordinal);
            HasDynamicPorts = factory.HasDynamicPorts;
            SupportsDeclaredOutputs = factory.SupportsDeclaredOutputs;
            DeclaredOutputsCollect = factory.DeclaredOutputsCollect;
            RunsWorkflowFrom = factory.RunsWorkflowFrom;
            TypeParameters = factory.TypeParameters.ToArray();
        }

        public string BlockTypeId { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string Category { get; }
        public IReadOnlyCollection<BlockPortDefinition> Inputs { get; }
        public IReadOnlyCollection<BlockPortDefinition> Outputs { get; }
        public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema { get; }
        public IReadOnlyCollection<string> SubCanvasNames { get; }
        public bool HasDynamicPorts { get; }
        public bool SupportsDeclaredOutputs { get; }
        public bool DeclaredOutputsCollect { get; }
        public string? RunsWorkflowFrom { get; }
        public IReadOnlyList<WorkflowTypeParameter> TypeParameters { get; }
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string name) => inputs[name];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string name) => outputs[name];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException("A catalogue snapshot cannot execute.");
    }

    private static IReadOnlyCollection<BlockPortResponse> Describe(IEnumerable<BlockPortDefinition> ports) =>
        ports.Select(p => new BlockPortResponse(
            p.Name, p.DisplayName, p.Type.ToString(), p.IsRequired, p.AllowedValues)
        {
            ScopeVariables = p.ScopeVariables.Select(v => new BlockScopeVariableResponse(v.Name, v.Type)).ToArray(),
            SecretValues = p.SecretValues,
        }).ToArray();
}
