using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Editor;

/// <summary>What a definition has to satisfy before it is stored. Refusing at the door, rather than at the
/// run that reads it back, is what keeps a stored graph one the engine can always load.</summary>
/// <remarks>Public because a host that writes definitions by another road — an import or a seed —
/// wants the same door. Every public check here runs the stored document reader first, on the raw text, and every
/// refusal is <see cref="InvalidWorkflowDefinitionException"/>. It admits documents the compiler refuses, an error policy among them, so a road that makes a version current runs <c>WorkflowCompilationService.EnsureAsync</c>
/// too.</remarks>
public static class WorkflowDefinitionAdmission
{
    /// <summary>Refuses what the stored document reader refuses (an absent or blank text included), one node id twice in
    /// a canvas, or a sub-workflow not chosen by a fixed id. A document the compiler only reports on is stored: half a graph.</summary>
    public static void Ensure(string? definitionJson) => Ensure(definitionJson, null, null);

    /// <summary>What <see cref="Ensure(string?)"/> refuses, and a parameter list the stored document reader
    /// refuses.</summary>
    public static void Ensure(string? definitionJson, string? inputParametersJson, string? outputParametersJson)
    {
        EnsureReadable(definitionJson, inputParametersJson, outputParametersJson);

        var typed = ReadAsTheEngineDoes(definitionJson!)!;
        EnsureNodeIdsUniquePerCanvas(typed);
        EnsureStatic(typed);
    }

    /// <summary>Refuses the three documents of a version when the stored document reader refuses any of them: a
    /// definition that is absent, blank or not one it reads, or a parameter list it does not read (none is an absent
    /// list, or <c>[]</c>; blank text and the JSON <c>null</c> are refused). Every library door that writes version text runs it; a host that writes a
    /// version by its own road, raw SQL or its own entities, runs it on the three texts first.</summary>
    public static void EnsureReadable(string? definitionJson, string? inputParametersJson, string? outputParametersJson)
    {
        var check = PortTypeNameCheck.Check(definitionJson, inputParametersJson, outputParametersJson);
        if (check.Unreadable.Count > 0)
            throw new InvalidWorkflowDefinitionException(string.Join(" ", check.Unreadable.Select(d => d.Message)));
    }

    /// <summary>Refuses a definition the stored document reader refuses, an absent or blank one included, then one
    /// whose sub-workflow nodes do not name their child by a fixed id, so a run's version manifest is complete by
    /// construction. The promotion paths run it over what is already stored.</summary>
    public static void EnsureSubWorkflowReferencesStatic(string? definitionJson)
    {
        EnsureReadable(definitionJson, null, null);
        EnsureStatic(ReadAsTheEngineDoes(definitionJson!)!);
    }

    private static void EnsureStatic(WorkflowDefinition typed)
    {
        try
        {
            WorkflowGraphHelpers.ValidateSubWorkflowReferencesStatic(typed);
        }
        catch (WorkflowValidationException ex)
        {
            throw new InvalidWorkflowDefinitionException(ex.Message, ex);
        }
    }

    private static WorkflowDefinition? ReadAsTheEngineDoes(string definitionJson)
    {
        try
        {
            return WorkflowDefinitionParse.FromText(definitionJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidWorkflowDefinitionException("it is not valid JSON.", ex);
        }
    }

    /// <summary>Refuses a definition that puts a block which may park the run inside a container nothing can put
    /// it back inside — one that does not declare <see cref="IWorkflowBlockFactory.OnlyDrivesItsSubCanvases"/>.
    /// The engine refuses the same placement when a run reaches it; this is that refusal at the save, where the
    /// author can still move the block.</summary>
    /// <remarks>Refuses what the stored document reader refuses of the definition first, an absent or blank one
    /// included. Says nothing about a block id nothing is registered under: such a run fails on that node anyway,
    /// so the door never guesses at one. A durable sleep is not refused — where it cannot park it is served in the
    /// process, which is slower and not wrong.</remarks>
    public static void EnsureWaitsCanBeParked(string? definitionJson, IWorkflowBlocks blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        EnsureReadable(definitionJson, null, null);
        EnsureCanvas(ReadAsTheEngineDoes(definitionJson!)!.Nodes, blocks, refusing: null);
    }

    private static void EnsureCanvas(
        IReadOnlyCollection<WorkflowNodeDefinition> nodes, IWorkflowBlocks blocks, string? refusing)
    {
        foreach (var node in nodes)
        {
            var factory = blocks.Find(node.BlockTypeId);
            if (refusing is not null && factory?.CanSuspend == true)
            {
                throw new InvalidWorkflowDefinitionException(
                    $"block '{node.Id}' ({node.BlockTypeId}) may park the run and sits inside '{refusing}', which "
                    + "does more than drive its sub-canvases, so nothing could put the run back inside it. Move the "
                    + "block out of that container, or have that container declare that it only drives its "
                    + "sub-canvases.");
            }

            if (node.SubCanvases is not { Count: > 0 } canvases)
                continue;

            var deeper = refusing ?? (factory?.OnlyDrivesItsSubCanvases == true ? null : node.Id);
            foreach (var canvas in canvases.Values)
            {
                if (canvas?.Nodes is { } inner)
                    EnsureCanvas(inner, blocks, deeper);
            }
        }
    }

    private static void EnsureNodeIdsUniquePerCanvas(WorkflowDefinition canvas)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in canvas.Nodes)
        {
            if (node is null)
                continue;

            var id = node.Id ?? string.Empty;
            if (!seen.Add(id))
            {
                throw new InvalidWorkflowDefinitionException(id.Length == 0
                    ? "it contains several nodes without an id."
                    : $"it contains a duplicate node id '{id}' (rule duplicate-node-id).");
            }

            foreach (var subCanvas in (node.SubCanvases ?? []).Values)
            {
                if (subCanvas is not null)
                    EnsureNodeIdsUniquePerCanvas(subCanvas);
            }
        }
    }
}
