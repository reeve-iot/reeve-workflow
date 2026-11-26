using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>Every read of a workflow that answers a request: the workflow and its summary, its runs and
/// their steps, its versions and the diff between two of them. Reads only — nothing here writes.</summary>
public sealed class WorkflowCatalog(
    WorkflowDbContext workflowDb, IWorkflowBlocks blocks, WorkflowCompilationService compiler, IWorkflowDefinitionSecrets secrets) : IWorkflowCatalog
{
    public async Task<IReadOnlyCollection<WorkflowSummaryResponse>> GetWorkflowsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var executionStats = await workflowDb.WorkflowExecutionLogs
            .AsNoTracking()
            .GroupBy(e => e.WorkflowId)
            .Select(g => new
            {
                WorkflowId = g.Key,
                Count = g.Count(),
                LatestStartedAtUtc = g.Max(e => (DateTimeOffset?)e.StartedAtUtc),
                LatestStatus = g
                    .OrderByDescending(e => e.StartedAtUtc)
                    .Select(e => (WorkflowExecutionStatus?)e.Status)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var statsLookup = executionStats.ToDictionary(s => s.WorkflowId);

        var workflows = await workflowDb.Workflows
            .AsNoTracking()
            .Where(w => w.CreatedByUserId == userId)
            .OrderBy(w => w.Name)
            .Select(w => new
            {
                w.Id,
                w.Name,
                w.Description,
                w.HostMetadata,
                w.HasImportWarnings,
                w.ImportWarningsJson,
#pragma warning disable CS8602
                InputParametersJson = w.CurrentVersion != null ? w.CurrentVersion.InputParametersJson : w.DraftVersion!.InputParametersJson,
                OutputParametersJson = w.CurrentVersion != null ? w.CurrentVersion.OutputParametersJson : w.DraftVersion!.OutputParametersJson,
                w.UpdatedAtUtc,
                HasUnpublishedChanges = w.DraftVersionId != null,
                CurrentVersionNumber = w.CurrentVersion != null ? (int?)w.CurrentVersion.VersionNumber : null
            })
#pragma warning restore CS8602
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return workflows.Select(w =>
        {
            statsLookup.TryGetValue(w.Id, out var stats);
            return new WorkflowSummaryResponse(
                w.Id, w.Name, w.Description, w.HostMetadata,
                w.HasImportWarnings, w.ImportWarningsJson,
                HasDeclaredParameters(w.InputParametersJson, PortTypeNameSite.InputParameter),
                HasDeclaredParameters(w.OutputParametersJson, PortTypeNameSite.OutputParameter),
                w.UpdatedAtUtc,
                stats?.LatestStartedAtUtc,
                stats?.Count ?? 0,
                stats?.LatestStatus?.ToString(),
                w.HasUnpublishedChanges,
                w.CurrentVersionNumber);
        }).ToArray();
    }
    private static bool HasDeclaredParameters(string? json, PortTypeNameSite site) =>
        StoredWorkflowDocument.ReadParameters(json, site, out _) is { Count: > 0 };
    public async Task<WorkflowResponse> GetWorkflowAsync(Guid userId, Guid workflowId, CancellationToken cancellationToken = default)
    {
        var workflow = await workflowDb.Workflows
            .AsNoTracking()
            .Include(w => w.CurrentVersion)
            .Include(w => w.DraftVersion)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.CreatedByUserId == userId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowNotFoundException(workflowId);

        return MapToResponse(workflow, secrets);
    }
    private async Task EnsureWorkflowOwnershipAsync(Guid userId, Guid workflowId, CancellationToken cancellationToken)
    {
        var isOwner = await workflowDb.Workflows.AnyAsync(w => w.Id == workflowId && w.CreatedByUserId == userId, cancellationToken).ConfigureAwait(false);
        if (!isOwner)
            throw new WorkflowNotFoundException(workflowId);
    }
    public async Task<WorkflowImportValidationResult> ValidateWorkflowImportAsync(
        string? definitionJson, string? inputParametersJson, string? outputParametersJson,
        CancellationToken cancellationToken = default)
    {
        var check = await compiler.CheckAsync(definitionJson, inputParametersJson, outputParametersJson, cancellationToken)
            .ConfigureAwait(false);
        var refused = check.Diagnostics.Where(d => d.RuleId != CompilationRules.BlockTypeUnknown).ToList();
        if (refused.Count > 0)
            throw new WorkflowDoesNotCompileException(refused);

        var definition = StoredWorkflowDocument.Read(definitionJson, out _)!.Definition;
        var missingBlocks = new List<string>();
        var references = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in CanvasWalk.Frames(definition))
            ScanCanvas(frame.Canvas, missingBlocks, references);

        return new WorkflowImportValidationResult(
            missingBlocks,
            references.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyCollection<string>)[.. entry.Value],
                StringComparer.OrdinalIgnoreCase),
            [.. check.UnresolvedWorkflowIds.Select(id => id.ToString())],
            definitionJson!,
            inputParametersJson,
            outputParametersJson,
            secrets.PredictImportDrops(definitionJson!));
    }

    private void ScanCanvas(
        WorkflowDefinition canvas, List<string> missingBlocks, Dictionary<string, HashSet<string>> references)
    {
        foreach (var node in canvas.Nodes)
        {
            if (BoundaryBlockShapes.IsBoundary(node.BlockTypeId))
                continue;

            if (blocks.Find(node.BlockTypeId) is not { } factory)
            {
                if (!missingBlocks.Contains(node.BlockTypeId))
                    missingBlocks.Add(node.BlockTypeId);
                continue;
            }

            var portTypeByName = factory.Inputs.ToDictionary(p => p.Name, p => p.Type, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in node.Config ?? [])
            {
                var reachesMap = portTypeByName.TryGetValue(name, out var declared) && HoldsMap(declared);
                foreach (var id in IdsIn(value, reachesMap))
                    RememberReference(portTypeByName, name, id, references);
            }
        }

        var byId = new Dictionary<string, WorkflowNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in canvas.Nodes)
            byId.TryAdd(node.Id, node);

        foreach (var connection in canvas.GetConnections() ?? [])
        {
            if (connection.Mappings is not { } mappings
                || !byId.TryGetValue(connection.TargetNodeId, out var target)
                || blocks.Find(target.BlockTypeId) is not { } factory)
            {
                continue;
            }

            var portTypeByName = factory.Inputs.ToDictionary(p => p.Name, p => p.Type, StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in WorkflowGraphHelpers.ReadMappings(mappings).Where(m => m.IsLiteral))
            {
                var reachesMap = portTypeByName.TryGetValue(mapping.TargetInput, out var declared) && HoldsMap(declared);
                foreach (var id in IdsIn(mapping.LiteralValue, reachesMap))
                    RememberReference(portTypeByName, mapping.TargetInput, id, references);
            }
        }
    }

    private static IEnumerable<string> IdsIn(object? value, bool objects) => value switch
    {
        string text => [text],
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() is { } text ? [text] : [],
        JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().SelectMany(item => IdsIn(item, objects)),
        JsonElement { ValueKind: JsonValueKind.Object } members => objects ? members.EnumerateObject().SelectMany(m => IdsIn(m.Value, objects)) : [],
        System.Collections.IDictionary members => objects ? members.Values.Cast<object?>().SelectMany(m => IdsIn(m, objects)) : [],
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(item => IdsIn(item, objects)),
        _ => [],
    };

    private static bool HoldsMap(PortValueType type)
    {
        for (var term = PortTerm.Parse(type.Name, PortTermGrammar.Declaration); term.Kind == PortTermKind.Constructor; term = term.Arguments[^1])
        {
            if (term.IsMap)
                return true;
        }

        return false;
    }

    private static void RememberReference(
        Dictionary<string, PortValueType> portTypeByName, string portName, string id, Dictionary<string, HashSet<string>> references)
    {
        if (Guid.TryParse(id, out _) && portTypeByName.TryGetValue(portName, out var portType))
            Remember(references, portType, id);
    }

    /// <summary>Maps a versioned workflow, its definition as <paramref name="secrets"/> shows it. A row without a current or draft version is an invalid document resource.</summary>
    public static WorkflowResponse MapToResponse(Workflow w, IWorkflowDefinitionSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var version = w.CurrentVersion ?? w.DraftVersion;
        var (definition, inputs, outputs) = version is null
            ? throw new InvalidOperationException($"Workflow {w.Id} has no current or draft version.")
            : (version.DefinitionJson, version.InputParametersJson, version.OutputParametersJson);
        return new WorkflowResponse(
            w.Id, w.Name, w.Description, w.HostMetadata, secrets.Mask(definition), inputs, outputs,
            w.HasImportWarnings, w.ImportWarningsJson,
            w.ExecutionTimeoutSeconds, w.RetryPolicyJson,
            w.CreatedByUserId, w.CreatedAtUtc, w.UpdatedAtUtc,
            w.CurrentVersionId, w.DraftVersionId,
            w.CurrentVersion?.VersionNumber, w.DraftVersion?.VersionNumber,
            w.Mode.ToString(), w.MaxConcurrentRuns, w.MaxQueuedRuns, w.OverflowBehavior.ToString(),
            w.CheckpointPolicyJson)
        {
            DocumentsFrom = w.CurrentVersion is not null ? WorkflowDocumentsSource.Current : WorkflowDocumentsSource.Draft,
            Revision = w.Revision,
        };
    }
    public async Task<IReadOnlyCollection<WorkflowExecutionResponse>> GetExecutionsAsync(Guid userId, Guid workflowId, bool? hasErrors = null, string? status = null, CancellationToken cancellationToken = default)
    {
        var paged = await GetExecutionsPagedAsync(userId, workflowId, skip: 0, take: 100, hasErrors, status, cancellationToken).ConfigureAwait(false);
        return paged.Items;
    }
    /// <inheritdoc />
    public async Task<PagedWorkflowExecutionsResponse> GetExecutionsPagedAsync(
        Guid userId, Guid workflowId, int skip = 0, int take = 50,
        bool? hasErrors = null, string? status = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureWorkflowOwnershipAsync(userId, workflowId, cancellationToken).ConfigureAwait(false);

        (skip, take) = WorkflowPaging.ClampSkipTake(skip, take, 200);

        var query = BuildExecutionsQuery(workflowId, hasErrors, status);

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var page = WorkflowPaging.ApplySkipTake(
            query.OrderByDescending(e => e.StartedAtUtc), skip, take, 200);
        var executions = await page.ToListAsync(cancellationToken).ConfigureAwait(false);

        return new PagedWorkflowExecutionsResponse(
            executions.Select(MapExecutionResponse).ToArray(),
            totalCount,
            skip,
            take);
    }
    private IQueryable<WorkflowExecutionLog> BuildExecutionsQuery(
        Guid workflowId, bool? hasErrors, string? status)
    {
        var query = workflowDb.WorkflowExecutionLogs
            .AsNoTracking()
            .Where(e => e.WorkflowId == workflowId);

        if (hasErrors == true)
        {
            query = query.Where(e => e.FailureReason != null
                || workflowDb.WorkflowExecutionSteps.Any(s => s.ExecutionLogId == e.Id && s.ErrorJson != null));
        }

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<WorkflowExecutionStatus>(status, ignoreCase: true, out var parsed))
        {
            query = query.Where(e => e.Status == parsed);
        }

        return query;
    }
    public async Task<WorkflowExecutionDetailResponse> GetExecutionAsync(Guid userId, Guid workflowId, Guid executionId, CancellationToken cancellationToken = default)
    {
        await EnsureWorkflowOwnershipAsync(userId, workflowId, cancellationToken).ConfigureAwait(false);

        var execution = await workflowDb.WorkflowExecutionLogs
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == executionId && e.WorkflowId == workflowId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowExecutionNotFoundException(executionId);

        var entries = await workflowDb.WorkflowExecutionLogEntries
            .AsNoTracking()
            .Where(entry => entry.ExecutionLogId == executionId)
            .OrderBy(entry => entry.Timestamp)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var steps = await workflowDb.WorkflowExecutionSteps
            .AsNoTracking()
            .Where(s => s.ExecutionLogId == executionId && s.StartedAt >= execution.StartedAtUtc)
            .OrderBy(s => s.SortOrder)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var durationMs = execution.CompletedAtUtc.HasValue
            ? (execution.CompletedAtUtc.Value - execution.StartedAtUtc).TotalMilliseconds
            : (double?)null;

        return new WorkflowExecutionDetailResponse(
            execution.Id, execution.AutomationId, execution.WorkflowId,
            execution.TriggerId, execution.Status.ToString(),
            execution.StartedAtUtc, execution.CompletedAtUtc,
            execution.ErrorMessage, durationMs,
            entries.Select(e => new WorkflowExecutionLogEntryResponse(
                e.Id, e.NodeId, e.BlockTypeId, e.Level.ToString(),
                e.Message, e.DataJson, e.IterationIndex, e.CallStackJson, e.Timestamp)).ToArray(),
            steps.Select(s => new WorkflowExecutionStepResponse(
                s.Id, s.NodeId, s.NodeType, s.StartedAt, s.EndedAt,
                s.DurationMs, s.Status.ToString(), s.SortOrder,
                s.InputsJson, s.OutputsJson, s.ErrorMessage, s.ErrorStackTrace, s.ErrorJson)).ToArray(),
            execution.OutputsJson)
        {
            FailureReason = execution.FailureReason,
        };
    }
    public async Task<IReadOnlyCollection<WorkflowExecutionStepResponse>> GetExecutionReplayAsync(Guid userId, Guid workflowId, Guid executionId, CancellationToken cancellationToken = default)
    {
        await EnsureWorkflowOwnershipAsync(userId, workflowId, cancellationToken).ConfigureAwait(false);

        var execution = await workflowDb.WorkflowExecutionLogs
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == executionId && e.WorkflowId == workflowId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowExecutionNotFoundException(executionId);

        var steps = await workflowDb.WorkflowExecutionSteps
            .AsNoTracking()
            .Where(s => s.ExecutionLogId == executionId && s.StartedAt >= execution.StartedAtUtc)
            .OrderBy(s => s.SortOrder)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return steps.Select(s => new WorkflowExecutionStepResponse(
            s.Id, s.NodeId, s.NodeType, s.StartedAt, s.EndedAt,
            s.DurationMs, s.Status.ToString(), s.SortOrder,
            s.InputsJson, s.OutputsJson, s.ErrorMessage, s.ErrorStackTrace, s.ErrorJson)).ToArray();
    }
    public async Task<IReadOnlyCollection<WorkflowVersionSummaryResponse>> GetWorkflowVersionsAsync(Guid userId, Guid workflowId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        await EnsureWorkflowOwnershipAsync(userId, workflowId, cancellationToken).ConfigureAwait(false);

        var versions = await WorkflowPaging.ApplyPage(
                workflowDb.WorkflowVersions
                    .AsNoTracking()
                    .Where(v => v.WorkflowId == workflowId)
                    .OrderByDescending(v => v.VersionNumber),
                page, pageSize, 100)
            .Select(v => new WorkflowVersionSummaryResponse(
                v.Id, v.VersionNumber, v.CreatedByUserId, v.CreatedAtUtc,
                v.ChangeMessage, v.IsPublished, v.PublishedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return versions;
    }
    public async Task<WorkflowVersionResponse> GetWorkflowVersionAsync(Guid userId, Guid workflowId, int versionNumber, CancellationToken cancellationToken = default)
    {
        await EnsureWorkflowOwnershipAsync(userId, workflowId, cancellationToken).ConfigureAwait(false);

        var version = await workflowDb.WorkflowVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.WorkflowId == workflowId && v.VersionNumber == versionNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowVersionNotFoundException(workflowId, versionNumber);

        return MapToVersionResponse(version);
    }
    private WorkflowVersionResponse MapToVersionResponse(WorkflowVersion v) => new(
        v.Id, v.WorkflowId, v.VersionNumber, secrets.Mask(v.DefinitionJson),
        v.InputParametersJson, v.OutputParametersJson,
        v.CreatedByUserId, v.CreatedAtUtc, v.ChangeMessage, v.IsPublished, v.PublishedAtUtc);
    public async Task<WorkflowDiffResponse> DiffVersionsAsync(Guid userId, Guid workflowId, int versionA, int versionB, CancellationToken cancellationToken = default)
    {
        await EnsureWorkflowOwnershipAsync(userId, workflowId, cancellationToken).ConfigureAwait(false);

        var vA = await workflowDb.WorkflowVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.WorkflowId == workflowId && v.VersionNumber == versionA, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowVersionNotFoundException(workflowId, versionA);

        var vB = await workflowDb.WorkflowVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.WorkflowId == workflowId && v.VersionNumber == versionB, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowVersionNotFoundException(workflowId, versionB);

        return CalculateDiff(vA, vB);
    }
    private WorkflowDiffResponse CalculateDiff(WorkflowVersion vA, WorkflowVersion vB) =>
        StoredVersionComparison.Compare(
            new(vA.VersionNumber, vA.DefinitionJson, vA.InputParametersJson, vA.OutputParametersJson, secrets.Mask(vA.DefinitionJson)),
            new(vB.VersionNumber, vB.DefinitionJson, vB.InputParametersJson, vB.OutputParametersJson, secrets.Mask(vB.DefinitionJson)),
            blocks);

    private static void Remember(
        Dictionary<string, HashSet<string>> references, PortValueType portType, string id)
    {
        var term = PortTerm.Parse(portType.Name, PortTermGrammar.Declaration);
        var name = portType.Name;
        while (term.Kind == PortTermKind.Constructor && (term.IsMap || term.IsCollection && !term.IsNullable))
        {
            term = term.Arguments[^1];
            name = term.ToString();
        }

        if (!references.TryGetValue(name, out var ids))
            references[name] = ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ids.Add(id);
    }

    private static WorkflowExecutionResponse MapExecutionResponse(WorkflowExecutionLog e)
    {
        var durationMs = e.CompletedAtUtc.HasValue
            ? (e.CompletedAtUtc.Value - e.StartedAtUtc).TotalMilliseconds
            : (double?)null;

        return new WorkflowExecutionResponse(
            e.Id, e.AutomationId, e.WorkflowId, e.TriggerId,
            e.Status.ToString(), e.StartedAtUtc, e.CompletedAtUtc,
            e.ErrorMessage, durationMs)
        {
            FailureReason = e.FailureReason,
        };
    }
}
