using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor;

internal static class WorkflowChangeLogTag
{
    internal const string Value = "reeve-cluster: appended to change log";
}

/// <summary>The write side over the library's own context. Every method reads the workflow through the
/// owner's id first, so a caller who does not own one is told it is not there rather than that it is.</summary>
/// <remarks>Every method stages nothing on the context before it opens its unit of work through the seam: a
/// mutation staged on the shared context by a write the seam went on to refuse is one the host's next
/// <c>SaveChanges</c> would commit for it. Publish and rollback read and compile before the seam, with
/// <c>AsNoTracking</c>, and create and update compile after their commit, so no door compiles inside a transaction
/// it opened. Inside a transaction the host commits (<see cref="IWorkflowEditorTransaction.JoinsAnotherTransaction"/>),
/// nothing outside it can read what the door stores: create and update save without compiling or promoting
/// (<see cref="WorkflowEditResult.PublishDeferred"/>), and publish and rollback are refused.
/// <para>Publish, rollback, create and an auto-published save make a version current only when
/// <see cref="WorkflowCompilationService"/> answers it valid. A save that writes version text, a publish and a
/// rollback move <see cref="Workflow.Revision"/> by one in their first write to the workflow's rows; a promote
/// compares the revision it read and changes nothing when another write moved it
/// (<see cref="WorkflowDraftChangedException"/>).</para></remarks>
public sealed class WorkflowEditor(
    WorkflowDbContext context,
    IWorkflowEditorTransaction transaction,
    WorkflowCompilationService compiler,
    IWorkflowDefinitionSecrets secrets,
    TimeProvider? clock = null,
    IWorkflowBlocks? blocks = null,
    WorkflowEngineOptions? engine = null,
    IWorkflowNodeValidator? nodeValidator = null,
    ILogger<WorkflowEditor>? logger = null) : IWorkflowEditor
{
    private static readonly string[] NonTerminalStatuses =
    [
        nameof(WorkflowRunStateStatus.Pending),
        nameof(WorkflowRunStateStatus.Running),
        nameof(WorkflowRunStateStatus.Sleeping),
        nameof(WorkflowRunStateStatus.Waiting),
        nameof(WorkflowRunStateStatus.Suspended),
    ];

    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    private readonly WorkflowCompilationService compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));

    /// <inheritdoc />
    public async Task<WorkflowEditResult> CreateAsync(
        Guid userId, CreateWorkflowRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        WorkflowDefinitionAdmission.Ensure(request.DefinitionJson, request.InputParametersJson, request.OutputParametersJson);
        if (blocks is not null)
            WorkflowDefinitionAdmission.EnsureWaitsCanBeParked(request.DefinitionJson, blocks);
        EnsureWorkflowRowSettings(request.ExecutionTimeoutSeconds, request.RetryPolicyJson);
        EnsureCheckpointPolicyIsAllowed(request.CheckpointPolicyJson);
        var hostMetadata = StoredHostMetadata(request.HostMetadata);
        var origin = request.Origin ?? WorkflowDefinitionOrigin.Edited;
        if (!Enum.IsDefined(origin))
            throw new InvalidWorkflowEditRequestException("The origin is neither 'Edited' nor 'Imported'.");

        Workflow workflow;
        WorkflowVersion draft;
        WorkflowSecretAdmission admission;
        var workflowId = Guid.NewGuid();
        var hosts = transaction.JoinsAnotherTransaction;
        await using (var scope = await transaction.BeginAsync(new WorkflowWriteIntent(null, hostMetadata), cancellationToken).ConfigureAwait(false))
        {
            admission = await AdmitAsync(userId, workflowId, null, Admitted(request.DefinitionJson), origin, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            workflow = new Workflow(
                workflowId, request.Name, request.Description,
                userId, now, hostMetadata,
                request.ExecutionTimeoutSeconds, request.RetryPolicyJson);
            workflow.SetCheckpointPolicyJson(request.CheckpointPolicyJson, now);

            if (request.ImportWarningsJson is { Length: > 0 })
                workflow.SetImportWarnings(request.ImportWarningsJson);

            ApplyConcurrency(workflow, request.Mode, request.MaxConcurrentRuns,
                request.MaxQueuedRuns, request.OverflowBehavior, now);

            draft = new WorkflowVersion(
                Guid.NewGuid(), workflow.Id, versionNumber: 1, admission.Definition,
                request.InputParametersJson, request.OutputParametersJson, userId, now);

            context.Workflows.Add(workflow);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            workflow.SetDraftVersion(draft, now);
            context.WorkflowVersions.Add(draft);
            await SaveVersionAsync(workflow.Id, draft.VersionNumber, cancellationToken).ConfigureAwait(false);

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (hosts)
            return Reporting(Unpublished(WorkflowCatalog.MapToResponse(workflow, secrets), draft) with { PublishDeferred = true }, admission);
        return Reporting(await AfterSaveAsync(workflow, draft, savedRevision: 0, publish: true, cancellationToken).ConfigureAwait(false), admission);
    }

    /// <inheritdoc />
    public async Task<WorkflowEditResult> UpdateAsync(
        Guid userId, Guid workflowId, UpdateWorkflowRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureWorkflowRowSettings(request.ExecutionTimeoutSeconds, request.RetryPolicyJson);
        EnsureCheckpointPolicyIsAllowed(request.CheckpointPolicyJson);
        var hostMetadata = StoredHostMetadata(request.HostMetadata);

        Workflow workflow;
        WorkflowVersion? written = null, unchanged = null;
        (string, string?, string?)? compared = null;
        WorkflowSecretAdmission? secretsReport = null;
        var savedRevision = 0;
        var hosts = transaction.JoinsAnotherTransaction;
        await using (var scope = await transaction.BeginAsync(new WorkflowWriteIntent(workflowId, hostMetadata), cancellationToken).ConfigureAwait(false))
        {
            workflow = await LoadOwnedAsync(userId, workflowId, cancellationToken).ConfigureAwait(false);
            savedRevision = await MoveRevisionAsync(userId, workflowId, request.ExpectedRevision, cancellationToken).ConfigureAwait(false);
            Revised(workflow, savedRevision);
            var decision = await VersionTextWriteAsync(userId, workflow, request, cancellationToken).ConfigureAwait(false);
            var (text, definition, admission) = (decision.Text, decision.Definition, decision.Admission);
            secretsReport = admission;
            if (text == VersionText.None && request.AutoPublish && workflow.DraftVersion is { } repeated)
            {
                WorkflowDefinitionAdmission.EnsureReadable(repeated.DefinitionJson, repeated.InputParametersJson, repeated.OutputParametersJson);
                WorkflowDefinitionAdmission.EnsureSubWorkflowReferencesStatic(repeated.DefinitionJson);
            }

            var now = clock.GetUtcNow();
            workflow.Update(request.Name, request.Description, now, hostMetadata,
                request.ExecutionTimeoutSeconds, request.RetryPolicyJson);
            workflow.SetCheckpointPolicyJson(request.CheckpointPolicyJson, now);

            ApplyConcurrency(workflow, request.Mode, request.MaxConcurrentRuns,
                request.MaxQueuedRuns, request.OverflowBehavior, now);

            if (text == VersionText.DiscardDraft)
            {
                await EnsureDraftAsync(workflowId, workflow.DraftVersionId, cancellationToken).ConfigureAwait(false);
                var discarded = workflow.DraftVersion!;
                if (SwapsReferences(admission) && !await context.WorkflowVersions.AsNoTracking()
                        .AnyAsync(v => v.Id == discarded.Id && v.DefinitionJson == discarded.DefinitionJson, cancellationToken).ConfigureAwait(false))
                    throw new WorkflowDraftChangedException(workflowId);
                workflow.SetDraftVersion(null, now);
            }
            else if (text == VersionText.Write && workflow.DraftVersion is { } draft)
            {
                var definitionJson = Admitted(definition);
                var guardText = SwapsReferences(admission);
                await EnsureDraftAsync(workflowId, draft.Id, cancellationToken).ConfigureAwait(false);
                var rewritten = await context.WorkflowVersions
                    .Where(v => v.Id == draft.Id && !v.IsPublished && (!guardText || v.DefinitionJson == draft.DefinitionJson))
                    .TagWith(WorkflowChangeLogTag.Value)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(v => v.DefinitionJson, definitionJson)
                        .SetProperty(v => v.InputParametersJson, request.InputParametersJson)
                        .SetProperty(v => v.OutputParametersJson, request.OutputParametersJson), cancellationToken)
                    .ConfigureAwait(false);
                if (rewritten != 1)
                    throw new WorkflowDraftChangedException(workflowId);

                draft.UpdateDefinition(definitionJson, request.InputParametersJson, request.OutputParametersJson);
                AcceptDocuments(context.Entry(draft));
                written = draft;
            }
            else if (text == VersionText.Write)
            {
                await EnsureDraftAsync(workflowId, expectedDraft: null, cancellationToken, requireNoDraft: SwapsReferences(admission)).ConfigureAwait(false);
                written = new WorkflowVersion(
                    Guid.NewGuid(), workflowId, await NextVersionNumberAsync(workflowId, cancellationToken).ConfigureAwait(false),
                    Admitted(definition), request.InputParametersJson, request.OutputParametersJson,
                    userId, now, request.ChangeMessage);

                context.WorkflowVersions.Add(written);
                workflow.SetDraftVersion(written, now);
            }
            else if (text == VersionText.None && request.AutoPublish && workflow.DraftVersion is { } same)
            {
                unchanged = same;
                compared = (same.DefinitionJson, same.InputParametersJson, same.OutputParametersJson);
            }

            var inserted = written is not null && context.Entry(written).State == EntityState.Added;
            await SaveVersionAsync(workflowId, inserted ? written!.VersionNumber : null, cancellationToken).ConfigureAwait(false);

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var target = written ?? unchanged;
        if (target is null)
            return Reporting(new WorkflowEditResult(WorkflowCatalog.MapToResponse(workflow, secrets), null, null, VersionPublished: false), secretsReport);
        if (hosts)
            return Reporting(Unpublished(WorkflowCatalog.MapToResponse(workflow, secrets), target) with { PublishDeferred = request.AutoPublish }, secretsReport);

        return Reporting(
            await AfterSaveAsync(workflow, target, savedRevision, request.AutoPublish, cancellationToken, compared).ConfigureAwait(false),
            secretsReport);
    }

    /// <inheritdoc />
    public async Task<WorkflowEditResult> PublishAsync(
        Guid userId, Guid workflowId, CancellationToken cancellationToken = default)
    {
        if (transaction.JoinsAnotherTransaction)
            throw new WorkflowPublishInHostTransactionException(workflowId);

        var read = await ReadAsync(userId, workflowId, cancellationToken).ConfigureAwait(false);
        var draft = read.DraftVersion ?? throw new NoDraftVersionToPublishException(workflowId);

        WorkflowDefinitionAdmission.EnsureReadable(draft.DefinitionJson, draft.InputParametersJson, draft.OutputParametersJson);
        WorkflowDefinitionAdmission.EnsureSubWorkflowReferencesStatic(draft.DefinitionJson);

        var judged = await JudgeAsync(draft, workflowId, draft.Id, WorkflowValidationPhase.Publish, cancellationToken).ConfigureAwait(false);
        if (!judged.IsValid)
            throw new WorkflowDoesNotCompileException(judged.Diagnostics);

        var promoted = await PromoteAsync(workflowId, draft.Id, read.Revision, cancellationToken).ConfigureAwait(false);
        return await PublishedAsync(workflowId, Promoted(read, draft, promoted), draft, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkflowEditResult> RollbackAsync(
        Guid userId, Guid workflowId, int versionNumber, CancellationToken cancellationToken = default)
    {
        if (transaction.JoinsAnotherTransaction)
            throw new WorkflowPublishInHostTransactionException(workflowId);

        var rows = await (
                from w in Owned(userId, workflowId)
                join v in context.WorkflowVersions.Where(v => v.VersionNumber == versionNumber) on w.Id equals v.WorkflowId
                    into targets
                from found in targets.DefaultIfEmpty()
                select new { Workflow = w, Target = found })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowNotFoundException(workflowId);
        var read = rows.Workflow;

        if (read.CurrentVersion?.VersionNumber == versionNumber)
            throw new CannotRollbackToActiveVersionException(versionNumber);

        var target = rows.Target ?? throw new WorkflowVersionNotFoundException(workflowId, versionNumber);

        WorkflowDefinitionAdmission.EnsureReadable(target.DefinitionJson, target.InputParametersJson, target.OutputParametersJson);
        WorkflowDefinitionAdmission.EnsureSubWorkflowReferencesStatic(target.DefinitionJson);

        var now = clock.GetUtcNow();
        var copyId = Guid.NewGuid();
        var (verdict, program) = await CompileAsync(
            new WorkflowVersion(copyId, workflowId, 0, target.DefinitionJson, target.InputParametersJson,
                target.OutputParametersJson, userId, now),
            workflowId, target.Id, cancellationToken).ConfigureAwait(false);
        if (!verdict.IsValid)
            throw new WorkflowDoesNotCompileException(verdict.Diagnostics);

        WorkflowVersion copy;
        WorkflowSecretAdmission admission;
        await using (var scope = await transaction.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            admission = await RestoredAsync(userId, workflowId, (read.DraftVersion ?? read.CurrentVersion)?.DefinitionJson, target.DefinitionJson, cancellationToken)
                .ConfigureAwait(false);

            var bumped = await context.Workflows
                .Where(w => w.Id == workflowId && w.Revision == read.Revision)
                .TagWith(WorkflowChangeLogTag.Value)
                .ExecuteUpdateAsync(set => set.SetProperty(w => w.Revision, w => w.Revision + 1), cancellationToken)
                .ConfigureAwait(false);
            if (bumped != 1)
                throw await ConflictAsync(workflowId, cancellationToken).ConfigureAwait(false);

            copy = new WorkflowVersion(
                copyId, workflowId, await NextVersionNumberAsync(workflowId, cancellationToken).ConfigureAwait(false),
                target.DefinitionJson, target.InputParametersJson, target.OutputParametersJson,
                userId, now, $"Rollback to version {versionNumber}");
            context.WorkflowVersions.Add(copy);
            await SaveVersionAsync(workflowId, copy.VersionNumber, cancellationToken).ConfigureAwait(false);

            await context.Workflows.Where(w => w.Id == workflowId)
                .TagWith(WorkflowChangeLogTag.Value)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(w => w.DraftVersionId, (Guid?)copyId)
                    .SetProperty(w => w.UpdatedAtUtc, now), cancellationToken)
                .ConfigureAwait(false);

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var findings = nodeValidator is not null && program is not null
            ? await ValidateNodesAsync(nodeValidator, copy, program, WorkflowValidationPhase.Publish, cancellationToken).ConfigureAwait(false)
            : [];
        if (findings.Count > 0)
        {
            var stored = await LoadOwnedAsync(userId, workflowId, cancellationToken).ConfigureAwait(false);
            return Reporting(Unpublished(WorkflowCatalog.MapToResponse(stored, secrets), copy) with { Diagnostics = findings }, admission);
        }

        var promoted = await PromoteAsync(workflowId, copyId, read.Revision + 1, cancellationToken).ConfigureAwait(false);
        return Reporting(await PublishedAsync(workflowId, Promoted(read, copy, promoted), copy, cancellationToken).ConfigureAwait(false), admission);
    }

    /// <inheritdoc />
    public async Task<WorkflowEditResult> DeleteAsync(
        Guid userId, Guid workflowId, CancellationToken cancellationToken = default)
    {
        await using var scope = await transaction.BeginAsync(cancellationToken).ConfigureAwait(false);
        var workflow = await LoadOwnedAsync(userId, workflowId, cancellationToken).ConfigureAwait(false);

        var response = WorkflowCatalog.MapToResponse(workflow, secrets);
        var now = clock.GetUtcNow();

        await context.WorkflowExecutionStates
            .Where(e => e.WorkflowId == workflowId && !NonTerminalStatuses.Contains(e.Status))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        var active = await context.WorkflowExecutionStates
            .CountAsync(e => e.WorkflowId == workflowId, cancellationToken).ConfigureAwait(false);
        if (active > 0)
            throw new WorkflowHasActiveExecutionsException(workflowId, active);

        await context.WorkflowSuspensions.Where(s => s.WorkflowId == workflowId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        workflow.SetCurrentVersion(null, now);
        workflow.SetDraftVersion(null, now);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        context.Workflows.Remove(workflow);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new WorkflowEditResult(response, VersionId: null, VersionNumber: null, VersionPublished: false);
    }

    private async Task<WorkflowEditResult> AfterSaveAsync(
        Workflow saved, WorkflowVersion written, int savedRevision, bool publish, CancellationToken cancellationToken,
        (string, string?, string?)? compared = null)
    {
        Workflow? back;
        Judgement? judged = null;
        Promotion promoted;
        try
        {
            back = await ReadBackAsync(saved.Id, cancellationToken).ConfigureAwait(false);
            if (back is null)
                return Unpublished(WorkflowCatalog.MapToResponse(saved, secrets), written) with { Fault = new WorkflowNotFoundException(saved.Id) };

            var stillOurs = back.Revision == savedRevision && back.DraftVersionId == written.Id
                && (compared is not { } texts || Holds(back.DraftVersion, texts));
            if (publish && !stillOurs)
                throw new WorkflowDraftChangedException(saved.Id);
            if (stillOurs)
            {
                judged = await JudgeAsync(back.DraftVersion!, saved.Id, written.Id,
                    publish ? WorkflowValidationPhase.Publish : WorkflowValidationPhase.Save, cancellationToken).ConfigureAwait(false);
            }

            if (!publish || judged is not { IsValid: true })
                return Unpublished(WorkflowCatalog.MapToResponse(back, secrets), written) with { Diagnostics = judged?.Diagnostics ?? [] };

            promoted = await PromoteAsync(saved.Id, written.Id, back.Revision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception fault) when (fault is not WorkflowDraftChangedException)
        {
            return Unpublished(WorkflowCatalog.MapToResponse(saved, secrets), written) with
            {
                Diagnostics = judged?.Diagnostics ?? [],
                Fault = fault,
            };
        }

        return await PublishedAsync(saved.Id, Promoted(back, back.DraftVersion!, promoted), written, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record Judgement(CompilationVerdict Verdict, IReadOnlyList<CompilationDiagnostic> Findings)
    {
        public bool IsValid => Verdict.IsValid && Findings.Count == 0;

        public IReadOnlyList<CompilationDiagnostic> Diagnostics => [.. Verdict.Diagnostics, .. Findings];
    }

    private async Task<Judgement> JudgeAsync(
        WorkflowVersion version, Guid workflowId, Guid named, WorkflowValidationPhase phase, CancellationToken cancellationToken)
    {
        var (verdict, program) = await CompileAsync(version, workflowId, named, cancellationToken).ConfigureAwait(false);
        if (nodeValidator is null || !verdict.IsValid || program is null)
            return new Judgement(verdict, []);
        return new Judgement(verdict, await ValidateNodesAsync(nodeValidator, version, program, phase, cancellationToken).ConfigureAwait(false));
    }

    private async Task<(CompilationVerdict Verdict, WorkflowProgram? Program)> CompileAsync(
        WorkflowVersion version, Guid workflowId, Guid named, CancellationToken cancellationToken)
    {
        try
        {
            if (nodeValidator is null)
                return (await compiler.EnsureAsync(version, null, cancellationToken).ConfigureAwait(false), null);
            return await compiler.EnsureWithProgramAsync(version, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fault)
        {
            throw new WorkflowCompilationUnansweredException(workflowId, named,
                $"Whether version '{named}' of workflow '{workflowId}' compiles could not be answered, so it was not "
                + "made current. Nothing was published; asking again may succeed.", fault);
        }
    }

    private async Task<IReadOnlyList<CompilationDiagnostic>> ValidateNodesAsync(
        IWorkflowNodeValidator validator, WorkflowVersion version, WorkflowProgram program, WorkflowValidationPhase phase,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkflowValidationFinding>? findings;
        try
        {
            findings = await validator.ValidateAsync(WorkflowNodeValidation.Request(version, program, phase), cancellationToken)
                .ConfigureAwait(false);
            if (findings is null)
                logger?.LogWarning("The host's node validator answered nothing for version {VersionId}.", version.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fault)
        {
            logger?.LogWarning(fault, "The host's node validator threw for version {VersionId}.", version.Id);
            findings = null;
        }

        return findings is null
            ? [new CompilationDiagnostic(WorkflowEditorFindingCodes.HostValidatorFaulted,
                "The host's node validator could not be asked, so the version was not judged by it.")]
            : [.. findings.Select(f => f.ToDiagnostic())];
    }

    private async Task<WorkflowEditResult> PublishedAsync(
        Guid workflowId, WorkflowResponse committed, WorkflowVersion version, CancellationToken cancellationToken)
    {
        try
        {
            return new WorkflowEditResult(await ResponseAsync(workflowId, cancellationToken).ConfigureAwait(false),
                version.Id, version.VersionNumber, VersionPublished: true);
        }
        catch (Exception fault)
        {
            return new WorkflowEditResult(committed, version.Id, version.VersionNumber, VersionPublished: true) { Fault = fault };
        }
    }

    private WorkflowResponse Promoted(Workflow read, WorkflowVersion version, Promotion promoted)
    {
        read.SetCurrentVersion(version, promoted.At);
        read.SetDraftVersion(null, promoted.At);
        return WorkflowCatalog.MapToResponse(read, secrets) with { Revision = promoted.Revision };
    }

    private readonly record struct Promotion(DateTimeOffset At, int Revision);

    private static WorkflowEditResult Unpublished(WorkflowResponse response, WorkflowVersion written) =>
        new(response, written.Id, written.VersionNumber, VersionPublished: false);

    private async Task<Promotion> PromoteAsync(Guid workflowId, Guid draftId, int revision, CancellationToken cancellationToken)
    {
        await using var scope = await transaction.BeginAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        var moved = await context.Workflows
            .Where(w => w.Id == workflowId && w.Revision == revision && w.DraftVersionId == draftId)
            .TagWith(WorkflowChangeLogTag.Value)
            .ExecuteUpdateAsync(set => set
                .SetProperty(w => w.CurrentVersionId, (Guid?)draftId)
                .SetProperty(w => w.DraftVersionId, (Guid?)null)
                .SetProperty(w => w.Revision, w => w.Revision + 1)
                .SetProperty(w => w.UpdatedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        if (moved != 1)
            throw await ConflictAsync(workflowId, cancellationToken).ConfigureAwait(false);

        var published = await context.WorkflowVersions
            .Where(v => v.Id == draftId && !v.IsPublished)
            .TagWith(WorkflowChangeLogTag.Value)
            .ExecuteUpdateAsync(set => set
                .SetProperty(v => v.IsPublished, true)
                .SetProperty(v => v.PublishedAtUtc, (DateTimeOffset?)now), cancellationToken)
            .ConfigureAwait(false);
        if (published != 1)
            throw new WorkflowDraftChangedException(workflowId);

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new Promotion(now, revision + 1);
    }

    private async Task EnsureDraftAsync(Guid workflowId, Guid? expectedDraft, CancellationToken cancellationToken, bool requireNoDraft = false)
    {
        if (expectedDraft is null && !requireNoDraft)
            return;

        var row = await context.Workflows.AsNoTracking().Where(w => w.Id == workflowId)
            .Select(w => new { w.DraftVersionId }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null || row.DraftVersionId != expectedDraft)
            throw await ConflictAsync(workflowId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> MoveRevisionAsync(Guid userId, Guid workflowId, int? expected, CancellationToken cancellationToken)
    {
        var rows = context.Workflows.Where(w => w.Id == workflowId && w.CreatedByUserId == userId);
        if (expected is { } at)
            rows = rows.Where(w => w.Revision == at);
        var moved = await rows
            .TagWith(WorkflowChangeLogTag.Value)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.Revision, w => w.Revision + 1), cancellationToken)
            .ConfigureAwait(false);
        if (moved == 1)
            return expected is { } read
                ? read + 1
                : await context.Workflows.AsNoTracking().Where(w => w.Id == workflowId).Select(w => w.Revision)
                    .SingleAsync(cancellationToken).ConfigureAwait(false);

        throw expected is { } stale && await context.Workflows.AsNoTracking()
                .AnyAsync(w => w.Id == workflowId && w.CreatedByUserId == userId, cancellationToken).ConfigureAwait(false)
            ? new WorkflowRevisionMismatchException(workflowId, stale)
            : new WorkflowNotFoundException(workflowId);
    }

    private void Revised(Workflow workflow, int revision)
    {
        var property = context.Entry(workflow).Property(w => w.Revision);
        property.CurrentValue = revision;
        property.OriginalValue = revision;
        property.IsModified = false;
    }

    private async Task<Exception> ConflictAsync(Guid workflowId, CancellationToken cancellationToken) =>
        await context.Workflows.AsNoTracking().AnyAsync(w => w.Id == workflowId, cancellationToken).ConfigureAwait(false)
            ? new WorkflowDraftChangedException(workflowId)
            : new WorkflowNotFoundException(workflowId);

    private async Task<Workflow?> ReadBackAsync(Guid workflowId, CancellationToken cancellationToken) =>
        await context.Workflows.AsNoTracking()
            .Include(w => w.CurrentVersion)
            .Include(w => w.DraftVersion)
            .SingleOrDefaultAsync(w => w.Id == workflowId, cancellationToken).ConfigureAwait(false);

    private async Task<Workflow> ReadAsync(Guid userId, Guid workflowId, CancellationToken cancellationToken) =>
        await Owned(userId, workflowId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new WorkflowNotFoundException(workflowId);

    private IQueryable<Workflow> Owned(Guid userId, Guid workflowId) =>
        context.Workflows.AsNoTracking()
            .Include(w => w.CurrentVersion)
            .Include(w => w.DraftVersion)
            .Where(w => w.Id == workflowId && w.CreatedByUserId == userId);

    private async Task<WorkflowResponse> ResponseAsync(Guid workflowId, CancellationToken cancellationToken) =>
        WorkflowCatalog.MapToResponse(await ReadBackAsync(workflowId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowNotFoundException(workflowId), secrets);

    private enum VersionText
    {
        None,
        DiscardDraft,
        Write,
    }

    private async Task<TextDecision> VersionTextWriteAsync(
        Guid userId, Workflow workflow, UpdateWorkflowRequest request, CancellationToken cancellationToken)
    {
        var (draft, current) = (workflow.DraftVersion, workflow.CurrentVersion);
        var sent = (request.DefinitionJson, request.InputParametersJson, request.OutputParametersJson);
        if (Holds(draft ?? current, sent))
            return new(VersionText.None, null, null);
        if (draft is not null && Holds(current, sent))
        {
            var restored = await RestoredAsync(userId, workflow.Id, draft.DefinitionJson, current!.DefinitionJson, cancellationToken).ConfigureAwait(false);
            return new(VersionText.DiscardDraft, null, restored);
        }

        WorkflowDefinitionAdmission.Ensure(request.DefinitionJson, request.InputParametersJson, request.OutputParametersJson);
        if (blocks is not null)
            WorkflowDefinitionAdmission.EnsureWaitsCanBeParked(request.DefinitionJson, blocks);
        var admission = await AdmitAsync(userId, workflow.Id, (draft ?? current)?.DefinitionJson, Admitted(request.DefinitionJson), WorkflowDefinitionOrigin.Edited, cancellationToken)
            .ConfigureAwait(false);
        var definition = admission.Definition;
        if (draft is null && current is null)
            return new(VersionText.Write, definition, admission);

        var stored = await StoredDocumentText.AsStoredAsync(
            context, definition, request.InputParametersJson, request.OutputParametersJson, cancellationToken)
            .ConfigureAwait(false);
        var text = Holds(draft ?? current, stored) ? VersionText.None
            : draft is not null && Holds(current, stored) ? VersionText.DiscardDraft
            : VersionText.Write;
        return new(text, definition, admission);
    }

    private sealed record TextDecision(VersionText Text, string? Definition, WorkflowSecretAdmission? Admission);

    private async Task<WorkflowSecretAdmission> AdmitAsync(
        Guid userId, Guid workflowId, string? replaced, string definition, WorkflowDefinitionOrigin origin, CancellationToken cancellationToken)
    {
        try
        {
            return await secrets.AdmitAsync(new WorkflowSecretSave(userId, workflowId, origin), replaced, definition, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowSecretSaveRefusedException refusal)
        {
            throw new InvalidWorkflowEditRequestException(refusal.Message, refusal.Code);
        }
    }

    private async Task<WorkflowSecretAdmission> RestoredAsync(
        Guid userId, Guid workflowId, string? replaced, string definition, CancellationToken cancellationToken)
    {
        try
        {
            return await secrets.AdmitRestoredAsync(new WorkflowSecretSave(userId, workflowId, WorkflowDefinitionOrigin.Edited), replaced, definition, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowSecretSaveRefusedException refusal)
        {
            throw new InvalidWorkflowEditRequestException(refusal.Message, refusal.Code);
        }
    }

    private static bool SwapsReferences(WorkflowSecretAdmission? admission) =>
        admission is { } a && a.BoundReferences.Count + a.DroppedReferences.Count > 0;

    private static WorkflowEditResult Reporting(WorkflowEditResult result, WorkflowSecretAdmission? admission) =>
        admission is null ? result : result with
        {
            BoundReferences = admission.BoundReferences, DroppedReferences = admission.DroppedReferences, DroppedSecrets = admission.DroppedSecrets,
        };

    private static string Admitted(string? definitionJson) =>
        definitionJson ?? throw new InvalidWorkflowDefinitionException("it is absent.");

    private static bool Holds(WorkflowVersion? version, (string? Definition, string? Inputs, string? Outputs) texts) =>
        version is not null
        && texts.Definition == version.DefinitionJson
        && texts.Inputs == version.InputParametersJson
        && texts.Outputs == version.OutputParametersJson;

    private async Task SaveVersionAsync(Guid workflowId, int? versionNumber, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();
            if (!await context.Workflows.AsNoTracking().AnyAsync(w => w.Id == workflowId, CancellationToken.None).ConfigureAwait(false))
                throw new WorkflowNotFoundException(workflowId);
            throw;
        }
        catch (DbUpdateException exception) when (versionNumber is { } number)
        {
            context.ChangeTracker.Clear();
            var taken = await context.WorkflowVersions.AsNoTracking().AnyAsync(
                v => v.WorkflowId == workflowId && v.VersionNumber == number, CancellationToken.None)
                .ConfigureAwait(false);

            if (taken)
                throw new WorkflowVersionConflictException(workflowId, number, exception);
            throw;
        }
    }

    private async Task<Workflow> LoadOwnedAsync(Guid userId, Guid workflowId, CancellationToken cancellationToken)
    {
        await RefreshUnchangedCopiesAsync(workflowId, cancellationToken).ConfigureAwait(false);
        return await context.Workflows
            .Include(w => w.CurrentVersion)
            .Include(w => w.DraftVersion)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.CreatedByUserId == userId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new WorkflowNotFoundException(workflowId);
    }

    private async Task RefreshUnchangedCopiesAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var copies = context.ChangeTracker.Entries<Workflow>()
            .Where(e => e.State == EntityState.Unchanged && e.Entity.Id == workflowId).Cast<EntityEntry>()
            .Concat(context.ChangeTracker.Entries<WorkflowVersion>()
                .Where(e => e.State == EntityState.Unchanged && e.Entity.WorkflowId == workflowId))
            .ToList();
        foreach (var entry in copies)
        {
            if (await entry.GetDatabaseValuesAsync(cancellationToken).ConfigureAwait(false) is not { } row)
                continue;
            entry.CurrentValues.SetValues(row);
            entry.OriginalValues.SetValues(row);
            entry.State = EntityState.Unchanged;
        }
    }

    private static void AcceptDocuments(EntityEntry<WorkflowVersion> entry)
    {
        foreach (var name in new[] { nameof(WorkflowVersion.DefinitionJson), nameof(WorkflowVersion.InputParametersJson), nameof(WorkflowVersion.OutputParametersJson) })
        {
            var property = entry.Property(name);
            property.OriginalValue = property.CurrentValue;
            property.IsModified = false;
        }
    }

    private async Task<int> NextVersionNumberAsync(Guid workflowId, CancellationToken cancellationToken) =>
        (await context.WorkflowVersions
            .Where(v => v.WorkflowId == workflowId)
            .MaxAsync(v => (int?)v.VersionNumber, cancellationToken).ConfigureAwait(false) ?? 0) + 1;

    private static void EnsureWorkflowRowSettings(int? executionTimeoutSeconds, string? retryPolicyJson)
    {
        if (WorkflowRowSettings.Refusal(executionTimeoutSeconds, retryPolicyJson) is { } refusal)
            throw new InvalidWorkflowEditRequestException(refusal);
    }

    private static IReadOnlyDictionary<string, string>? StoredHostMetadata(IReadOnlyDictionary<string, string>? requested) =>
        WorkflowHostMetadata.Refusal(requested) is { } refusal
            ? throw new InvalidWorkflowEditRequestException(refusal, WorkflowSaveRefusalCodes.HostMetadataInvalid)
            : WorkflowHostMetadata.Normalize(requested);

    private void EnsureCheckpointPolicyIsAllowed(string? checkpointPolicyJson)
    {
        if (WorkflowCheckpointPolicy.Read(checkpointPolicyJson, WorkflowGraphHelpers.JsonOptions, out var mode) is { } unreadable)
            throw new InvalidWorkflowEditRequestException(unreadable);

        if (mode is null || engine is null)
            return;

        if (WorkflowCheckpointPolicy.RefusedBy(mode, engine.RunHeartbeatInterval, engine.Recovery.AbandonedAfter) is { } refusal)
            throw new InvalidWorkflowEditRequestException(refusal);
    }

    private static void ApplyConcurrency(Workflow workflow, string? mode, int? maxConcurrentRuns,
        int? maxQueuedRuns, string? overflowBehavior, DateTimeOffset now)
    {
        if (mode is null && maxConcurrentRuns is null && maxQueuedRuns is null && overflowBehavior is null)
            return;

        var effectiveMode = workflow.Mode;
        if (mode is not null && !TryParseName(mode, out effectiveMode))
        {
            throw new InvalidWorkflowEditRequestException(
                $"'{mode}' is not a workflow mode. Expected one of: {string.Join(", ", Enum.GetNames<WorkflowMode>())}.");
        }

        var effectiveOverflow = workflow.OverflowBehavior;
        if (overflowBehavior is not null && !TryParseName(overflowBehavior, out effectiveOverflow))
        {
            throw new InvalidWorkflowEditRequestException(
                $"'{overflowBehavior}' is not an overflow behaviour. Expected one of: "
                + string.Join(", ", Enum.GetNames<WorkflowOverflowBehavior>()) + ".");
        }

        workflow.ConfigureConcurrency(effectiveMode, maxConcurrentRuns ?? workflow.MaxConcurrentRuns,
            maxQueuedRuns ?? workflow.MaxQueuedRuns, effectiveOverflow, now);
    }

    private static bool TryParseName<TEnum>(string text, out TEnum value) where TEnum : struct, Enum
    {
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                return Enum.TryParse(name, out value);
        }
        value = default;
        return false;
    }
}
