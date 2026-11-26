using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>Where the compiler reads block shapes. The service derives one from the registered catalogue, so that
/// what is read and what is hashed are one declaration.</summary>
public interface IBlockShapeSource
{
    BlockShape? Find(string blockTypeId);
}

/// <summary>The shape source over a registered catalogue, read once at construction; the compilation service builds a new
/// one when the catalogue's generation moves.</summary>
/// <remarks>Each factory is read through <see cref="BlockShape.From"/>, so a broken declaration or a reserved or repeated block type id throws.</remarks>
public sealed class CatalogueBlockShapeSource : IBlockShapeSource
{
    private readonly Dictionary<string, BlockShape> byId;

    public CatalogueBlockShapeSource(IWorkflowBlocks blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        byId = new Dictionary<string, BlockShape>(StringComparer.OrdinalIgnoreCase);
        foreach (var factory in blocks.GetAll())
        {
            var shape = BlockShape.From(factory);
            if (BoundaryBlockShapes.IsReserved(shape.BlockTypeId))
                throw new InvalidOperationException(BoundaryBlockShapes.ReservedMessage(shape.BlockTypeId));

            if (!byId.TryAdd(shape.BlockTypeId, shape))
            {
                throw new InvalidOperationException(
                    $"Two block factories are registered under block type id '{shape.BlockTypeId}'. "
                    + "The compiler cannot bind a node to one of two shapes; fix the catalogue's registration.");
            }
        }
    }

    public BlockShape? Find(string blockTypeId) =>
        byId.TryGetValue(blockTypeId, out var shape) ? shape : null;
}
