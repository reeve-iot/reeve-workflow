using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>Every gate a run passes before it exists. Split from execution because each refusal here happens
/// before the run's own row does. A rejected fire leaves an audit trail, and a start refused because a version it would
/// run does not compile, or could not be judged, leaves one ended row saying so.</summary>
/// <remarks>It takes the <see cref="WorkflowCompilationService"/> the engine's other doors use, and the
/// journal the refusal's row is written through. A new start's arguments are read as its parameters' types and
/// judged, with its nodes, by the host's validators, between the compile and the lease.</remarks>
public sealed class WorkflowRunAdmission(
    IWorkflowStore store,
    IWorkflowBlocks blocks,
    WorkflowCompilationService compiler,
    IWorkflowRunJournal journal,
    IWorkflowExecutionGate executionGate,
    IWorkflowAuthorization authorization,
    IWorkflowAuditSink auditSink,
    IWorkflowMetrics metrics,
    ILogger logger,
    IWorkflowFireDedup? fireDedup = null,
    WorkflowAdmissionOptions? options = null,
    IWorkflowStartAdmission? startAdmission = null,
    IWorkflowNodeValidator? nodeValidator = null,
    IWorkflowStartArgumentValidator? argumentValidator = null)
{
    private readonly WorkflowAdmissionOptions _options = options ?? new WorkflowAdmissionOptions();

    /// <summary>The deepest call depth a run is admitted at: a root run is 0, and ten workflows may call down from it.
    /// The same bound <c>SubWorkflowBlock</c> has always put on its own chain.</summary>
    public const int MaxWorkflowCallDepth = 10;

    /// <summary>Runs the gates in the order their costs demand: the free checks first, the version reads next, then the
    /// fire-dedup claim (the one write before the host is asked), the host's start admission, the scripting gate, the
    /// compile and the lease. The claim comes before the rest so that one event reaching many replicas is judged once,
    /// bar Single, which skips the claim and has its refusal's row claimed instead, per workflow and key; with no key, on
    /// one replica, with no fire dedup (an admission built by hand), or where the claim fails open, each refusal leaves
    /// its own row. The host's refusal comes before the scripting audit and the compile row, and the lease comes last,
    /// because a refused run must not supersede or hold a place.</summary>
    public Task<WorkflowAdmissionResult> AdmitAsync(
        WorkflowAdmissionRequest request,
        Func<Workflow, WorkflowRunCoordinator> coordinatorFor,
        CancellationToken cancellationToken) =>
        AdmitAsync(request, coordinatorFor, parkedSleeps: null, onQueued: null, cancellationToken);

    /// <summary>The same gates, for a host whose runs can park on a sleep: the lease is then also refused to a
    /// Single run while one of the workflow's runs is parked on one.</summary>
    /// <remarks><c>onQueued</c> is handed to the coordinator: called once a Queued run's place is fixed.</remarks>
    public async Task<WorkflowAdmissionResult> AdmitAsync(
        WorkflowAdmissionRequest request,
        Func<Workflow, WorkflowRunCoordinator> coordinatorFor,
        IWorkflowParkedSleepStore? parkedSleeps,
        Action? onQueued,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(coordinatorFor);

        if (executionGate.IsExecutionSuspended)
        {
            logger.LogInformation(
                "Workflow {WorkflowId} execution skipped because execution is suspended.", request.WorkflowId);
            return WorkflowAdmissionResult.Refused(WorkflowAdmissionOutcomes.Skipped, "execution_suspended");
        }

        if (request.CallDepth > MaxWorkflowCallDepth)
        {
            logger.LogWarning(
                "Workflow {WorkflowId} refused at call depth {CallDepth}, past the limit of {Limit}.",
                request.WorkflowId, request.CallDepth, MaxWorkflowCallDepth);
            throw new WorkflowCallDepthExceededException(request.WorkflowId, request.CallDepth, MaxWorkflowCallDepth);
        }

        var workflow = await store.LoadAsync(request.WorkflowId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowNotFoundException(request.WorkflowId);

        if (workflow.CurrentVersion == null)
            throw new WorkflowValidationException("Workflow has no published version.");

        if (workflow.HasImportWarnings)
            throw new WorkflowValidationException(
                "Workflow cannot be executed because it has import references this host could not resolve. " +
                "Edit the workflow to fix the references, or re-import with the correct mappings.");

        var effectiveVersion = workflow.CurrentVersion!;
        if (request.PinnedVersionId.HasValue && request.PinnedVersionId.Value != effectiveVersion.Id)
        {
            effectiveVersion = await store.LoadVersionAsync(request.PinnedVersionId.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new PinnedWorkflowVersionNotFoundException(request.WorkflowId, request.PinnedVersionId.Value);
        }

        var pinManifest = request.PinnedVersions ?? await WorkflowVersionManifestBuilder.BuildAsync(
            request.WorkflowId, effectiveVersion.Id, WorkflowVersionDataCache.Get(effectiveVersion),
            (childId, ct) => store.LoadCurrentVersionOfAsync(childId, ct),
            cancellationToken).ConfigureAwait(false);

        var coordinator = coordinatorFor(workflow);
        if (!coordinator.TryPinAdmission(out var admissionPin))
        {
            coordinator = coordinatorFor(workflow);
            if (!coordinator.TryPinAdmission(out admissionPin))
                throw new InvalidOperationException("The workflow coordinator was retired during admission.");
        }
        using var pinnedAdmission = admissionPin;

        if (await IsCollapsedDuplicateFireAsync(request, coordinator.Mode, cancellationToken).ConfigureAwait(false))
            return WorkflowAdmissionResult.Refused(WorkflowAdmissionOutcomes.Dropped, "fire_dedup");

        if (request.StartKind is { } startKind)
            await AskHostAsync(request, startKind, effectiveVersion.Id, cancellationToken).ConfigureAwait(false);

        await EnforceScriptingGateAsync(workflow, effectiveVersion, cancellationToken).ConfigureAwait(false);

        var rowClaim = RefusalRowClaim(request, coordinator.Mode);
        var judgedPrograms = new List<(WorkflowVersion Version, WorkflowProgram Program)>();
        var program = await EnsureCompilesAsync(request, rowClaim, effectiveVersion, pinManifest, judgedPrograms,
            ownVersionOnly: request.PinnedVersions is not null, cancellationToken).ConfigureAwait(false);

        StartArguments? started = null;
        if (request.StartKind is { } kind)
        {
            started = await ReadArgumentsAsync(request, kind, rowClaim, effectiveVersion, program, cancellationToken).ConfigureAwait(false);
            await ValidateNodesAsync(request, rowClaim, effectiveVersion, pinManifest, judgedPrograms, cancellationToken)
                .ConfigureAwait(false);
        }

        WorkflowRunDecision decision;
        try
        {
            decision = await coordinator.AcquireAsync(parkedSleeps, onQueued, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowConcurrencyException ex)
        {
            await WriteAdmissionAuditAsync(workflow, WorkflowAuditEventKinds.RunDropped,
                WorkflowAdmissionOutcomes.Errored, ex.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (decision.Lease == null)
        {
            var auditKind = decision.Outcome switch
            {
                WorkflowAdmissionOutcomes.Skipped => WorkflowAuditEventKinds.RunSkipped,
                WorkflowAdmissionOutcomes.Dropped => WorkflowAuditEventKinds.RunDropped,
                _ => WorkflowAuditEventKinds.RunSkipped
            };
            await WriteAdmissionAuditAsync(workflow, auditKind, decision.Outcome,
                decision.Reason ?? decision.Outcome, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Workflow {WorkflowId} run not started: mode={Mode} outcome={Outcome} reason={Reason}",
                request.WorkflowId, decision.Mode, decision.Outcome, decision.Reason);
            return WorkflowAdmissionResult.Refused(decision.Outcome, decision.Reason);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (decision.Outcome == WorkflowAdmissionOutcomes.Restarted)
            {
                await WriteAdmissionAuditAsync(workflow, WorkflowAuditEventKinds.RunRestarted,
                    decision.Outcome, "restart_mode_supersession", cancellationToken).ConfigureAwait(false);
            }
            else if (decision.Outcome == WorkflowAdmissionOutcomes.Queued)
            {
                await WriteAdmissionAuditAsync(workflow, WorkflowAuditEventKinds.RunQueued,
                    decision.Outcome, "queued_then_started", cancellationToken).ConfigureAwait(false);
            }

            try
            {
                metrics.RecordExecutionStarted();
            }
            catch (Exception metricEx)
            {
                logger.LogWarning(metricEx, "Workflow {WorkflowId}: the start of a run could not be counted.", request.WorkflowId);
            }

            return WorkflowAdmissionResult.Admitted(
                workflow, effectiveVersion, decision, pinManifest,
                JsonSerializer.Serialize(pinManifest, WorkflowGraphHelpers.JsonOptions), program, started);
        }
        catch
        {
            await decision.Lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task AskHostAsync(
        WorkflowAdmissionRequest request, WorkflowStartKind kind, Guid versionId, CancellationToken cancellationToken)
    {
        if (startAdmission is null)
            return;

        WorkflowStartDecision? decision;
        try
        {
            decision = await startAdmission.DecideAsync(new WorkflowStartRequest(request.WorkflowId, versionId, kind)
            {
                ParentExecutionId = request.ParentExecutionId,
                AutomationId = request.AutomationId,
                TriggerId = request.TriggerId,
                CallDepth = request.CallDepth
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fault)
        {
            logger.LogWarning(fault, "Workflow {WorkflowId} start refused: the host's start admission threw.", request.WorkflowId);
            throw new WorkflowStartRefusedException(request.WorkflowId, WorkflowStartRefusalCodes.AdmissionFaulted,
                "Whether this workflow may start could not be answered.", fault);
        }

        if (decision is null)
        {
            logger.LogWarning("Workflow {WorkflowId} start refused: the host's start admission answered nothing.", request.WorkflowId);
            throw new WorkflowStartRefusedException(request.WorkflowId, WorkflowStartRefusalCodes.AdmissionFaulted,
                "Whether this workflow may start could not be answered.");
        }

        if (decision.IsAdmitted)
            return;

        logger.LogInformation("Workflow {WorkflowId} start ({Kind}) refused by the host: {Code}.",
            request.WorkflowId, kind, decision.Code);
        throw new WorkflowStartRefusedException(request.WorkflowId, decision.Code!, decision.Message!);
    }

    private async Task<StartArguments> ReadArgumentsAsync(
        WorkflowAdmissionRequest request, WorkflowStartKind kind, string? rowClaim, WorkflowVersion version,
        WorkflowProgram program, CancellationToken cancellationToken)
    {
        var signature = program.Canvases.First(canvas => canvas.CanvasPath.IsRoot).Signature;

        var vocabulary = compiler.Options.Vocabulary;
        var read = WorkflowStartArguments.Check(signature, request.Arguments, vocabulary);
        var findings = new List<WorkflowValidationFinding>(read.Findings);
        var payload = request.TriggerPayload;
        var terms = new Dictionary<string, ClosedPortType>(StringComparer.Ordinal);
        if (payload is { Count: > 0 })
        {
            var trigger = WorkflowStartArguments.TriggerNamespace(compiler.Options);
            try
            {
                payload = WorkflowStartArguments.ReadPayload(payload, trigger, vocabulary, findings, terms);
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                logger.LogWarning(fault, "Workflow {WorkflowId} start refused: the trigger namespace's check threw.", request.WorkflowId);
                throw new WorkflowStartRefusedException(request.WorkflowId, WorkflowStartRefusalCodes.AdmissionFaulted,
                    "Whether this workflow may start could not be answered.", fault);
            }
        }

        if (findings.Count == 0 && argumentValidator is not null && read.HostArguments.Count > 0)
        {
            var asked = new WorkflowStartArgumentsRequest(
                new WorkflowStartRequest(request.WorkflowId, version.Id, kind)
                {
                    ParentExecutionId = request.ParentExecutionId,
                    AutomationId = request.AutomationId,
                    TriggerId = request.TriggerId,
                    CallDepth = request.CallDepth
                }, read.HostArguments);
            findings.AddRange(await AskValidatorAsync(request, "start-argument validator",
                ct => argumentValidator.ValidateAsync(asked, ct), cancellationToken).ConfigureAwait(false));
        }

        if (findings.Count > 0)
            throw await RefuseAsync(request, rowClaim, version, WorkflowFailureReasons.StartArgumentsInvalid, version.Id, findings)
                .ConfigureAwait(false);

        return new StartArguments(read.Values, request.TriggerPayload is null ? null : payload, terms);
    }

    private async Task ValidateNodesAsync(
        WorkflowAdmissionRequest request, string? rowClaim, WorkflowVersion runVersion,
        IReadOnlyDictionary<Guid, Guid> pinManifest, List<(WorkflowVersion Version, WorkflowProgram Program)> judged,
        CancellationToken cancellationToken)
    {
        if (nodeValidator is null)
            return;

        if (request.PinnedVersions is not null)
        {
            var read = new Dictionary<Guid, WorkflowVersion> { [runVersion.Id] = runVersion };
            var at = (Workflow: request.WorkflowId, Version: runVersion.Id);
            try
            {
                var reach = await WorkflowVersionManifestBuilder.BuildAsync(request.WorkflowId, runVersion.Id,
                    WorkflowVersionDataCache.Get(runVersion), async (workflowId, ct) =>
                    {
                        if (!pinManifest.TryGetValue(workflowId, out var versionId))
                            return null;
                        at = (workflowId, versionId);
                        var version = await store.LoadVersionAsync(versionId, ct).ConfigureAwait(false)
                            ?? throw new PinnedWorkflowVersionNotFoundException(workflowId, versionId);
                        read[versionId] = version;
                        return (versionId, version.DefinitionJson);
                    }, cancellationToken).ConfigureAwait(false);
                foreach (var (workflowId, versionId) in reach)
                {
                    if (versionId == runVersion.Id)
                        continue;
                    at = (workflowId, versionId);
                    var (verdict, program) = await compiler.EnsureAdmittingAsync(
                        read[versionId], pinManifest, read, needsProgram: true, cancellationToken).ConfigureAwait(false);
                    if (verdict.IsValid && program is not null)
                        judged.Add((read[versionId], program));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception fault)
            {
                throw await UnansweredAsync(request, rowClaim, runVersion, at.Workflow, at.Version, fault).ConfigureAwait(false);
            }
        }

        foreach (var (version, program) in judged)
        {
            var asked = WorkflowNodeValidation.Request(version, program, WorkflowValidationPhase.Admission);
            var findings = await AskValidatorAsync(request, "node validator",
                ct => nodeValidator.ValidateAsync(asked, ct), cancellationToken).ConfigureAwait(false);
            if (findings.Count > 0)
                throw await RefuseAsync(request, rowClaim, runVersion, WorkflowFailureReasons.NodeValidationRefused, version.Id, findings)
                    .ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<WorkflowValidationFinding>> AskValidatorAsync(
        WorkflowAdmissionRequest request, string which,
        Func<CancellationToken, ValueTask<IReadOnlyList<WorkflowValidationFinding>>> ask, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await ask(cancellationToken).ConfigureAwait(false);
            if (answer is not null)
                return answer;
            logger.LogWarning("Workflow {WorkflowId} start refused: the host's {Validator} answered nothing.", request.WorkflowId, which);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fault)
        {
            logger.LogWarning(fault, "Workflow {WorkflowId} start refused: the host's {Validator} threw.", request.WorkflowId, which);
            throw new WorkflowStartRefusedException(request.WorkflowId, WorkflowStartRefusalCodes.AdmissionFaulted,
                "Whether this workflow may start could not be answered.", fault);
        }

        throw new WorkflowStartRefusedException(request.WorkflowId, WorkflowStartRefusalCodes.AdmissionFaulted,
            "Whether this workflow may start could not be answered.");
    }

    private async Task<WorkflowStartFindingsException> RefuseAsync(
        WorkflowAdmissionRequest request, string? rowClaim, WorkflowVersion runVersion, string reason, Guid judgedVersionId,
        IReadOnlyList<WorkflowValidationFinding> findings)
    {
        var refusal = new WorkflowStartFindingsException(request.WorkflowId, judgedVersionId, reason, findings);
        logger.LogWarning("Workflow {WorkflowId} run refused: {Reason}, the first finding is {Code}.",
            request.WorkflowId, reason, findings[0].Code);
        await RecordRefusalAsync(request, rowClaim, runVersion, reason, refusal.Message,
            row => [.. findings.Select(f => Entry(row, f, request.WorkflowId, judgedVersionId))]).ConfigureAwait(false);
        return refusal;
    }

    private static WorkflowExecutionLogEntry Entry(Guid row, WorkflowValidationFinding finding, Guid workflowId, Guid versionId) =>
        new(Guid.NewGuid(), row, finding.NodeId ?? string.Empty, string.Empty, LogEntryLevel.Error,
            Cut($"{finding.Code}: {finding.Message}", EntryMessageLimit),
            JsonSerializer.Serialize(new
            {
                RuleId = finding.Code, finding.NodeId, PortName = finding.Port, finding.Parameter,
                CanvasPath = finding.CanvasPath?.Segments ?? [], WorkflowId = workflowId, WorkflowVersionId = versionId
            }, JsonSerializerOptions.Web),
            null, null, DateTimeOffset.UtcNow);

    private async Task<WorkflowProgram> EnsureCompilesAsync(
        WorkflowAdmissionRequest request, string? rowClaim, WorkflowVersion runVersion,
        IReadOnlyDictionary<Guid, Guid> pinManifest, List<(WorkflowVersion Version, WorkflowProgram Program)> programs,
        bool ownVersionOnly, CancellationToken cancellationToken)
    {
        var read = new Dictionary<Guid, WorkflowVersion> { [runVersion.Id] = runVersion };
        var judged = new List<(Guid WorkflowId, WorkflowVersion Version)> { (request.WorkflowId, runVersion) };
        foreach (var (workflowId, versionId) in pinManifest.Where(_ => !ownVersionOnly))
        {
            if (read.ContainsKey(versionId))
                continue;

            var version = await ReadPinnedAsync(request, rowClaim, runVersion, workflowId, versionId, cancellationToken)
                .ConfigureAwait(false);
            read[versionId] = version;
            judged.Add((workflowId, version));
        }

        WorkflowProgram? rootProgram = null;
        foreach (var (workflowId, version) in judged)
        {
            CompilationVerdict verdict;
            try
            {
                var root = ReferenceEquals(version, runVersion);
                (verdict, var program) = await compiler.EnsureAdmittingAsync(
                        version, pinManifest, read, root || nodeValidator is not null, cancellationToken)
                    .ConfigureAwait(false);
                if (root)
                    rootProgram = program;
                if (program is not null && nodeValidator is not null)
                    programs.Add((version, program));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception fault)
            {
                throw await UnansweredAsync(request, rowClaim, runVersion, workflowId, version.Id, fault).ConfigureAwait(false);
            }

            if (verdict.IsValid)
                continue;

            var refusal = new WorkflowDoesNotCompileException(verdict.Diagnostics, workflowId, version.Id);
            logger.LogWarning(
                "Workflow {WorkflowId} run refused: version {VersionId} of workflow {RefusedWorkflowId} does not compile ({RuleId}).",
                request.WorkflowId, version.Id, workflowId, verdict.Diagnostics[0].RuleId);
            await RecordRefusalAsync(request, rowClaim, runVersion, WorkflowFailureReasons.DidNotCompile, refusal.Message,
                row => [.. verdict.Diagnostics.Select(d => Entry(row, d, workflowId, version.Id))]).ConfigureAwait(false);
            throw refusal;
        }

        return rootProgram ?? throw new InvalidOperationException($"Version {runVersion.Id} compiled and answered no program.");
    }

    private async Task<WorkflowVersion> ReadPinnedAsync(
        WorkflowAdmissionRequest request, string? rowClaim, WorkflowVersion runVersion, Guid workflowId, Guid versionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await store.LoadVersionAsync(versionId, cancellationToken).ConfigureAwait(false)
                ?? throw new PinnedWorkflowVersionNotFoundException(workflowId, versionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fault)
        {
            throw await UnansweredAsync(request, rowClaim, runVersion, workflowId, versionId, fault).ConfigureAwait(false);
        }
    }

    private async Task<WorkflowCompilationUnansweredException> UnansweredAsync(
        WorkflowAdmissionRequest request, string? rowClaim, WorkflowVersion runVersion, Guid workflowId, Guid versionId,
        Exception fault)
    {
        var refusal = new WorkflowCompilationUnansweredException(workflowId, versionId, fault);
        logger.LogWarning(fault,
            "Workflow {WorkflowId} run refused: whether version {VersionId} of workflow {JudgedWorkflowId} compiles could not be answered.",
            request.WorkflowId, versionId, workflowId);
        var said = $"{refusal.Message} The fault was a {fault.GetType().FullName}; this host's log has it.";
        await RecordRefusalAsync(request, rowClaim, runVersion, WorkflowFailureReasons.CompilationUnanswered, refusal.Message,
            row => [new WorkflowExecutionLogEntry(Guid.NewGuid(), row, string.Empty, string.Empty, LogEntryLevel.Error,
                Cut(said, EntryMessageLimit), null, null, null, DateTimeOffset.UtcNow)]).ConfigureAwait(false);
        return refusal;
    }

    private const int RowMessageLimit = 2000;
    private const int EntryMessageLimit = 4000;

    private async Task RecordRefusalAsync(
        WorkflowAdmissionRequest request, string? rowClaim, WorkflowVersion runVersion, string reason, string message,
        Func<Guid, IReadOnlyList<WorkflowExecutionLogEntry>> entriesOf)
    {
        if (rowClaim is not null && !await ClaimsRefusalRowAsync(request, rowClaim).ConfigureAwait(false))
            return;

        var now = DateTimeOffset.UtcNow;
        var row = new WorkflowExecutionLog(Guid.NewGuid(), request.AutomationId, request.WorkflowId, runVersion.Id,
            request.TriggerId, WorkflowExecutionStatus.Running, now);
        row.EndRefused(reason, now, Cut(message, RowMessageLimit));
        try
        {
            await journal.OpenRunAsync(row, CancellationToken.None).ConfigureAwait(false);
            await journal.SaveRunDetailsAsync(row.Id, recordLogEntries: true, entriesOf(row.Id), [], CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Workflow {WorkflowId}: the row of a refused start could not be written; the refusal still stands.",
                request.WorkflowId);
        }
    }

    private async Task<bool> ClaimsRefusalRowAsync(WorkflowAdmissionRequest request, string claim)
    {
        try
        {
            if (await fireDedup!.TryClaimFireAsync(claim, TimeSpan.FromSeconds(_options.FireDedupWindowSeconds),
                    CancellationToken.None).ConfigureAwait(false))
                return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Workflow {WorkflowId}: the row claim of a refused start failed ({FaultType}); writing the row.",
                request.WorkflowId, ex.GetType().Name);
            return true;
        }

        logger.LogInformation(
            "Workflow {WorkflowId} run refused; its row for this key is another start's, on this replica or another (keyFingerprint={FireKeyFingerprint}).",
            request.WorkflowId, fireDedup.Fingerprint(request.FireDedupKey!));
        return false;
    }

    private const string ReservedKeyPrefix = "reeveworkflow:";

    private static string FireClaim(string fireKey) =>
        fireKey.StartsWith(ReservedKeyPrefix, StringComparison.Ordinal) ? ReservedKeyPrefix + "fire:" + fireKey : fireKey;

    private string? RefusalRowClaim(WorkflowAdmissionRequest request, WorkflowMode mode) =>
        fireDedup != null && request.IsMultiReplica && request.FireDedupKey != null && mode == WorkflowMode.Single
            ? ReservedKeyPrefix + "refused-row:" + request.WorkflowId.ToString("N") + ":" + request.FireDedupKey
            : null;

    private static WorkflowExecutionLogEntry Entry(Guid row, CompilationDiagnostic diagnostic, Guid workflowId, Guid versionId) =>
        new(Guid.NewGuid(), row, diagnostic.NodeId ?? string.Empty, string.Empty, LogEntryLevel.Error,
            Cut($"{diagnostic.RuleId}: {diagnostic.Message}", EntryMessageLimit),
            JsonSerializer.Serialize(new
            {
                diagnostic.RuleId, diagnostic.NodeId, diagnostic.PortName, diagnostic.CanvasPath, diagnostic.BlockSource,
                WorkflowId = workflowId, WorkflowVersionId = versionId
            }, JsonSerializerOptions.Web),
            null, null, DateTimeOffset.UtcNow);

    private static string Cut(string value, int limit) => value.Length > limit ? value[..limit] : value;

    private async Task EnforceScriptingGateAsync(
        Workflow workflow, WorkflowVersion effectiveVersion, CancellationToken cancellationToken)
    {
        var cached = WorkflowVersionDataCache.Get(effectiveVersion);
        var hasPrivilegedBlock = blocks.ContainsPrivilegedBlock(cached.BlockTypes);
        if (!hasPrivilegedBlock)
            return;

        var ownerMayScript = await authorization.HasCapabilityAsync(
            workflow.CreatedByUserId, WorkflowCapabilities.Scripting, cancellationToken).ConfigureAwait(false);
        if (ownerMayScript)
            return;

        try
        {
            await auditSink.WriteAsync(new WorkflowAuditEvent(
                Kind: WorkflowAuditEventKinds.ScriptingDenied,
                ActorId: workflow.CreatedByUserId,
                SubjectId: workflow.Id,
                Data: new { Reason = "execution-time" }), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception auditEx)
        {
            logger.LogWarning(auditEx,
                "Failed to write the scripting-denied audit for workflow {WorkflowId}; the denial itself still stands.",
                workflow.Id);
        }

        throw new WorkflowScriptingPermissionException();
    }

    private async Task<bool> IsCollapsedDuplicateFireAsync(
        WorkflowAdmissionRequest request, WorkflowMode mode, CancellationToken cancellationToken)
    {
        if (fireDedup == null || !request.IsMultiReplica || request.FireDedupKey == null || mode == WorkflowMode.Single)
            return false;

        var window = TimeSpan.FromSeconds(_options.FireDedupWindowSeconds);
        if (await fireDedup.TryClaimFireAsync(FireClaim(request.FireDedupKey), window, cancellationToken).ConfigureAwait(false))
            return false;

        logger.LogInformation(
            "Workflow {WorkflowId} fire collapsed by fire-dedup (keyFingerprint={FireKeyFingerprint}); a sibling replica is running this physical event.",
            request.WorkflowId, fireDedup.Fingerprint(request.FireDedupKey));
        return true;
    }

    private async Task WriteAdmissionAuditAsync(
        Workflow workflow, string kind, string outcome, string? reason, CancellationToken cancellationToken)
    {
        try
        {
            await auditSink.WriteAsync(new WorkflowAuditEvent(
                Kind: kind,
                ActorId: null,
                SubjectId: workflow.Id,
                Data: new
                {
                    workflow.Mode,
                    workflow.MaxConcurrentRuns,
                    workflow.MaxQueuedRuns,
                    OverflowBehavior = workflow.OverflowBehavior.ToString(),
                    Outcome = outcome,
                    Reason = reason
                }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to write the {Kind} admission audit for workflow {WorkflowId}; the decision itself stands.",
                kind, workflow.Id);
        }
    }
}

/// <summary>What the caller knows before admission: which workflow, what fired it, and what the ambient run
/// scope pinned. Nothing here is read from storage — that is the store's job, once admission decides to.</summary>
public sealed record WorkflowAdmissionRequest(Guid WorkflowId)
{
    public Guid? TriggerId { get; init; }

    /// <summary>The automation that fired the run, recorded on the row of a start admission refuses.</summary>
    public Guid? AutomationId { get; init; }

    public string? FireDedupKey { get; init; }
    public Guid? PinnedVersionId { get; init; }
    public IReadOnlyDictionary<Guid, Guid>? PinnedVersions { get; init; }

    /// <summary>How many runs called down to this one. See <see cref="ReeveWorkflow.Abstractions.WorkflowRunPin.CallDepth"/>.</summary>
    public int CallDepth { get; init; }

    /// <summary>The door a new run came in by, which is also what makes the host's <see cref="IWorkflowStartAdmission"/>
    /// be asked. Null for a run already admitted that is being replayed, and for a request built by hand.</summary>
    public WorkflowStartKind? StartKind { get; init; }

    /// <summary>The run whose block started this one.</summary>
    public Guid? ParentExecutionId { get; init; }

    /// <summary>The arguments the start supplies, by parameter name, as the caller gave them: <see cref="JsonElement"/> values
    /// are read as their parameter's type, any other value is checked against its CLR type. Read only for a new start, which
    /// is one with a <see cref="StartKind"/>.</summary>
    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }

    /// <summary>The payload of the fire that started the run, whose fields admission reads into the terms the host's
    /// <c>trigger</c> namespace answers.</summary>
    public IReadOnlyDictionary<string, object?>? TriggerPayload { get; init; }

    /// <summary>Whether this deployment has peers. Fire-dedup is skipped on one replica: there is no fan-out
    /// to collapse, and claiming a key would only cost a round trip.</summary>
    public bool IsMultiReplica { get; init; }
}

/// <summary>Either a run that may proceed — with its lease, its version and its pin manifest — or a refusal
/// carrying the outcome the metric and the audit event already recorded.</summary>
public sealed class WorkflowAdmissionResult
{
    private WorkflowAdmissionResult(
        bool isAdmitted, string outcome, string? reason, Workflow? workflow, WorkflowVersion? version,
        WorkflowRunDecision? decision, IReadOnlyDictionary<Guid, Guid>? pinManifest, string? pinManifestJson)
    {
        IsAdmitted = isAdmitted;
        Outcome = outcome;
        Reason = reason;
        Workflow = workflow;
        EffectiveVersion = version;
        Decision = decision;
        PinManifest = pinManifest;
        PinManifestJson = pinManifestJson;
    }

    [MemberNotNullWhen(true, nameof(Program))]
    public bool IsAdmitted { get; }
    public string Outcome { get; }
    public string? Reason { get; }

    /// <summary>Set only when admitted. The lease on <see cref="Decision"/> MUST be disposed by the caller —
    /// it is the permit, and a lease nobody releases is a workflow that never runs again.</summary>
    public Workflow? Workflow { get; }
    public WorkflowVersion? EffectiveVersion { get; }
    public WorkflowRunDecision? Decision { get; }
    public IReadOnlyDictionary<Guid, Guid>? PinManifest { get; }
    public string? PinManifestJson { get; }

    internal WorkflowProgram? Program { get; private init; }

    internal StartArguments? Started { get; private init; }

    internal static WorkflowAdmissionResult Refused(string outcome, string? reason) =>
        new(false, outcome, reason, null, null, null, null, null);

    internal static WorkflowAdmissionResult Admitted(
        Workflow workflow, WorkflowVersion version, WorkflowRunDecision decision,
        IReadOnlyDictionary<Guid, Guid> pinManifest, string pinManifestJson, WorkflowProgram program, StartArguments? started) =>
        new(true, decision.Outcome, decision.Reason, workflow, version, decision, pinManifest, pinManifestJson)
        {
            Program = program,
            Started = started
        };
}

internal sealed record StartArguments(
    IReadOnlyDictionary<string, object?> Values, IReadOnlyDictionary<string, object?>? TriggerPayload,
    IReadOnlyDictionary<string, ClosedPortType> TriggerTerms);

/// <summary>The knobs admission reads rather than asks for.</summary>
public sealed class WorkflowAdmissionOptions
{
    /// <summary>How long one physical trigger event's fire key stays claimed across replicas.</summary>
    public double FireDedupWindowSeconds { get; init; } = 10;
}
