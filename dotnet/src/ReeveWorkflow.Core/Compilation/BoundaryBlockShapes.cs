using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

internal sealed class BoundaryBlockShapes : IBlockShapeSource
{
    private static readonly BlockShape Input = Boundary(WorkflowGraphHelpers.WorkflowInputBlockTypeId, "Workflow Input");
    private static readonly BlockShape Output = Boundary(WorkflowGraphHelpers.WorkflowOutputBlockTypeId, "Workflow Output");

    private readonly IBlockShapeSource catalogue;

    private BoundaryBlockShapes(IBlockShapeSource catalogue) => this.catalogue = catalogue;

    public static IBlockShapeSource Over(IBlockShapeSource shapes) =>
        shapes as BoundaryBlockShapes ?? new BoundaryBlockShapes(shapes);

    public static bool IsReserved(string? blockTypeId) =>
        string.Equals(blockTypeId, WorkflowGraphHelpers.WorkflowInputBlockTypeId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(blockTypeId, WorkflowGraphHelpers.WorkflowOutputBlockTypeId, StringComparison.OrdinalIgnoreCase);

    public static bool IsBoundary(string? blockTypeId) =>
        blockTypeId is WorkflowGraphHelpers.WorkflowInputBlockTypeId or WorkflowGraphHelpers.WorkflowOutputBlockTypeId;

    internal static BlockShape? For(string blockTypeId) => blockTypeId switch
    {
        WorkflowGraphHelpers.WorkflowInputBlockTypeId => Input,
        WorkflowGraphHelpers.WorkflowOutputBlockTypeId => Output,
        _ => null,
    };

    public static string ReservedMessage(string blockTypeId) =>
        $"Block type id '{blockTypeId}' is reserved: the library runs its WorkflowInput and WorkflowOutput nodes "
        + "itself and never reaches a factory registered under either id. Register the block under another id.";

    public BlockShape? Find(string blockTypeId) => blockTypeId switch
    {
        WorkflowGraphHelpers.WorkflowInputBlockTypeId => Input,
        WorkflowGraphHelpers.WorkflowOutputBlockTypeId => Output,
        _ => catalogue.Find(blockTypeId),
    };

    private static BlockShape Boundary(string blockTypeId, string displayName) =>
        new(blockTypeId, [], [], [], default, new Dictionary<string, IReadOnlyList<PortShape>>(),
            new Dictionary<string, IReadOnlyList<PortShape>>(), Source: null, DisplayName: displayName);
}
