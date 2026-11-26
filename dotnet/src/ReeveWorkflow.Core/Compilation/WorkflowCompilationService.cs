using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>The store seam's caller: answers "does this version compile" from an in-process memo, then a
/// store, and compiles only on a miss — resolving the port environment first, since that is the one I/O the
/// pure compiler is never allowed to do itself.</summary>
/// <remarks>A draft is edited in place (<c>WorkflowVersion.UpdateDefinition</c> keeps the same id), so a
/// verdict is keyed by <see cref="CompilationVerdict.WorkflowVersionId"/> together with a
/// <see cref="CompilationVerdict.DefinitionDigest"/> over the content actually compiled — never the version
/// id alone, which an edited-and-resaved draft would answer stale for. The in-process memo holds at most one
/// entry per version id, valid only while both digests still match, and is dropped whole the moment
/// <see cref="IWorkflowBlocks.Generation"/> moves: a resolved environment (a called workflow's signature) can
/// change with no generation bump at all, which is why every call re-resolves it and re-checks both digests
/// rather than trusting the generation alone.
/// <para>A verdict-store fault never blocks a verdict from being answered, unless it is a cancellation the
/// caller's own token asked for: a read fault is logged and treated as a miss, and a write fault is logged
/// and the freshly computed verdict is still returned, memoised "unsaved", so a later hit retries only the
/// save. A fault from a resolver or from <see cref="IWorkflowStore"/> always propagates: those are inputs the
/// verdict is a function of, not an optional side effect of recording one. So does a compile that could not answer, as
/// <see cref="CompilationFaultException"/>, logged once here and neither memoised nor saved. A store that declines with
/// <see cref="CompilationStoreUnavailableException"/> is logged once per service instance, without a stack
/// trace; an unsaved hit still retries its save, which such a store declines again.</para>
/// <para>The memo's concurrency contract is <see cref="VerdictMemo"/>'s: a write made after an await installs
/// only over the entry this call read before it, and is dropped otherwise.</para>
/// <para>A call opens at most one <see cref="IWorkflowRunScope"/> from the factory the engine reads, when it first
/// needs the workflow store, the verdict store or a resolver, and disposes it when the call returns. A hit on a saved
/// verdict whose definition has no node with ports resolved elsewhere needs none and opens none, and neither does one
/// run admission asks whose only such nodes run workflows whose pinned versions admission has read. From the scope, the
/// call reads <see cref="IWorkflowRunScope.Store"/>, and asks <see cref="IWorkflowRunScope.Services"/> for
/// <see cref="IWorkflowCompilationStore"/> and for every <see cref="INodePortResolver"/>; a provider that answers
/// null for either has none. So a host that replaces the run scope factory or fills the store's slot changes what
/// the compiler and the engine read together.</para></remarks>
public sealed class WorkflowCompilationService
{
    internal const int MemoCapacity = 10_000;

    private readonly IWorkflowBlocks blocks;
    private readonly IWorkflowRunScopeFactory scopes;
    private readonly CompilationOptions options;
    private readonly ILogger<WorkflowCompilationService> logger;
    private readonly TimeProvider time;

    private readonly IReadOnlyList<string> renderedNamespaces;

    private GenerationSnapshot snapshot;

    internal CompilationOptions Options => options;

    private int storeUnavailableLogged;

    /// <param name="blocks">The catalogue.</param>
    /// <param name="scopes">Where each call's store, verdict store and resolvers come from: the factory the engine
    /// reads. One scope is opened here too, so a duplicate or reserved resolver is refused when the service is built.</param>
    /// <param name="options">The type vocabulary and namespace checks; the library's own when null.</param>
    /// <param name="logger">Where store faults are logged.</param>
    /// <param name="timeProvider">The clock a verdict's <see cref="CompilationVerdict.CompiledAtUtc"/> is read
    /// from; the system clock when null.</param>
    /// <exception cref="ArgumentException">A resolver claims the library's own <c>SubWorkflow</c> block type, or two
    /// claim one block type. Every call that opens a scope refuses the same way.</exception>
    public WorkflowCompilationService(
        IWorkflowBlocks blocks,
        IWorkflowRunScopeFactory scopes,
        CompilationOptions? options = null,
        ILogger<WorkflowCompilationService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(scopes);

        this.blocks = blocks;
        this.scopes = scopes;
        this.options = options ?? new CompilationOptions();
        this.logger = logger ?? NullLogger<WorkflowCompilationService>.Instance;
        time = timeProvider ?? TimeProvider.System;
        using (var scope = scopes.CreateScope())
            _ = BuildResolverIndex(ResolversIn(scope));
        renderedNamespaces = this.options.RenderedNamespaces;
        snapshot = NewSnapshot();
    }

    /// <summary>Answers whether <paramref name="version"/> compiles against the current catalogue, from the
    /// cheapest source that still knows: the in-process memo, then the store, then a fresh compile — which
    /// resolves the environment, saves and remembers what it found.</summary>
    /// <param name="version">The version to compile — its id, definition and declared parameters. Its three
    /// text properties are read once, at the top of this call, and used throughout: reading them again later
    /// could see a draft mutated mid-call by another thread and save a verdict under a digest that no longer
    /// describes what was actually compiled.</param>
    /// <param name="pinManifest">Which child version each referenced workflow id is pinned to, for a run
    /// admission's own manifest; null resolves every reference against its callee's current published
    /// version, the reading a save or a publish uses. Under a manifest, a referenced child the manifest does
    /// not list is refused — never answered from that child's current version — because a run admitted under
    /// this manifest would refuse that same call at the door (<c>SubWorkflowBlock</c>) rather than run it.</param>
    /// <param name="cancellationToken">Handed to every I/O step and to the compile. When it is cancelled, the cancellation
    /// propagates, even from inside the verdict store; a store's own cancellation exception while it is still
    /// live is an ordinary store fault.</param>
    /// <exception cref="OperationCanceledException">Propagates uncaught.</exception>
    /// <exception cref="CompilationFaultException">An exception escaped a compiler phase; no verdict was recorded.</exception>
    public async Task<CompilationVerdict> EnsureAsync(
        WorkflowVersion version, IReadOnlyDictionary<Guid, Guid>? pinManifest, CancellationToken cancellationToken) =>
        (await EnsureRecordedAsync(version, pinManifest, cancellationToken).ConfigureAwait(false)).Verdict;

    internal async Task<(CompilationVerdict Verdict, WorkflowProgram? Program)> EnsureAdmittingAsync(
        WorkflowVersion version, IReadOnlyDictionary<Guid, Guid> pinManifest,
        IReadOnlyDictionary<Guid, WorkflowVersion> pinnedVersions, bool needsProgram, CancellationToken cancellationToken)
    {
        var (verdict, _, program) = await EnsureRecordedAsync(version, pinManifest, cancellationToken, needsProgram, pinnedVersions)
            .ConfigureAwait(false);
        return (verdict, program);
    }

    internal async Task<(CompilationVerdict Verdict, WorkflowProgram? Program)> EnsureWithProgramAsync(
        WorkflowVersion version, IReadOnlyDictionary<Guid, Guid>? pinManifest, CancellationToken cancellationToken)
    {
        var (verdict, _, program) = await EnsureRecordedAsync(version, pinManifest, cancellationToken, needsProgram: true)
            .ConfigureAwait(false);
        return (verdict, program);
    }

    internal async Task<(CompilationVerdict Verdict, bool Saved)> EnsureRecordedAsync(
        WorkflowVersion version, IReadOnlyDictionary<Guid, Guid>? pinManifest, CancellationToken cancellationToken,
        IReadOnlyDictionary<Guid, WorkflowVersion>? pinnedVersions = null)
    {
        var (verdict, saved, _) = await EnsureRecordedAsync(version, pinManifest, cancellationToken, false, pinnedVersions)
            .ConfigureAwait(false);
        return (verdict, saved);
    }

    private async Task<(CompilationVerdict Verdict, bool Saved, WorkflowProgram? Program)> EnsureRecordedAsync(
        WorkflowVersion version, IReadOnlyDictionary<Guid, Guid>? pinManifest, CancellationToken cancellationToken,
        bool needsProgram, IReadOnlyDictionary<Guid, WorkflowVersion>? pinnedVersions = null)
    {
        ArgumentNullException.ThrowIfNull(version);

        var versionId = version.Id;
        var definitionJson = version.DefinitionJson;
        var inputParametersJson = version.InputParametersJson;
        var outputParametersJson = version.OutputParametersJson;

        using var call = new CallScope(scopes);

        var current = CurrentSnapshot();
        var cachedVersion = WorkflowVersionDataCache.Get(versionId, definitionJson);
        var definitionDigest = cachedVersion.DigestFor(inputParametersJson, outputParametersJson);

        var document = cachedVersion.ReadStored(out var unreadable);
        var definition = document?.Definition ?? StoredWorkflowDocument.Empty.Definition;

        var environment = await ResolveEnvironmentAsync(
                call, definition, current.Shapes, pinManifest, pinnedVersions, null, cancellationToken)
            .ConfigureAwait(false);

        var helperPaths = cachedVersion.HelperPathsRead(current.Options.KeyShapes);
        var options = helperPaths.Count == 0
            ? current.Options
            : await OptionsWithHelperChecksAsync(definition, helperPaths, current.Options, cancellationToken).ConfigureAwait(false);

        var usedShapes = cachedVersion.ShapesUsed(current.Generation, current.Shapes);
        var renderedShapes = usedShapes.Select(s => current.ShapeRenders.GetOrAdd(s, DependencyFingerprint.RenderShape));
        var dependencyHash = DependencyFingerprint.Compute(
            renderedShapes, current.RenderedVocabulary, renderedNamespaces, environment, helperChecks: options.HelperChecks);

        var observed = current.Memo.Read(versionId);
        var hit = observed is not null && observed.DefinitionDigest == definitionDigest && observed.DependencyHash == dependencyHash;
        if (hit && (!needsProgram || !observed!.Verdict.IsValid || observed.Program is not null))
        {
            if (observed!.Saved)
                return (observed.Verdict, true, observed.Program);

            var retried = await SaveAsync(call.Inputs.Verdicts, observed.Verdict, cancellationToken).ConfigureAwait(false);
            if (retried)
                current.Memo.Replace(versionId, observed, observed.AsSaved());

            return (observed.Verdict, retried, observed.Program);
        }

        var stored = hit ? observed!.Verdict
            : await FindStoredAsync(call.Inputs.Verdicts, versionId, definitionDigest, dependencyHash, cancellationToken).ConfigureAwait(false);
        if (stored is not null && (!needsProgram || !stored.IsValid))
        {
            current.Memo.Replace(versionId, observed, new MemoEntry(definitionDigest, dependencyHash, stored, saved: true));
            return (stored, true, null);
        }

        var (verdict, program) = Compile(
            versionId, inputParametersJson, outputParametersJson, document, unreadable, current.Shapes,
            options.HelperChecksFilled ? options : options.WithHelperChecks(new Dictionary<HelperPath, NamespacePathCheckAnswer>()), environment,
            definitionDigest, dependencyHash, cancellationToken);

        var saved = stored is not null && hit ? observed!.Saved
            : stored is not null || await SaveAsync(call.Inputs.Verdicts, verdict, cancellationToken).ConfigureAwait(false);
        current.Memo.Replace(versionId, observed, new MemoEntry(definitionDigest, dependencyHash, verdict, saved, program));

        return (verdict, saved, program);
    }

    /// <summary>What an import is told of a document not stored anywhere (before it is a version): the same read,
    /// environment and compile <see cref="EnsureAsync"/> runs for a version, with nothing memoised or saved.</summary>
    /// <param name="definitionJson">The definition text, read by the one reader; absent is no document, which the reader refuses (<c>definition-unreadable</c>) as it refuses blank text.</param>
    /// <param name="inputParametersJson">The stored input parameter list the document is imported with; null is none.</param>
    /// <param name="outputParametersJson">The stored output parameter list; null is none.</param>
    /// <param name="cancellationToken">Handed to every I/O step and to the compile.</param>
    /// <returns>The diagnostics publish would answer, empty when the document compiles, except that a
    /// <c>SubWorkflow</c> whose callee is not stored (or has no current version) files no
    /// <c>node-ports-unresolved</c>: its id is in <see cref="ImportCompilation.UnresolvedWorkflowIds"/> instead.</returns>
    /// <exception cref="CompilationFaultException">An exception escaped a compiler phase; no answer exists.</exception>
    public async Task<ImportCompilation> CheckAsync(
        string? definitionJson, string? inputParametersJson, string? outputParametersJson, CancellationToken cancellationToken)
    {
        using var call = new CallScope(scopes);
        var current = CurrentSnapshot();
        var document = StoredWorkflowDocument.Read(definitionJson, out var unreadable);
        var definition = document?.Definition ?? StoredWorkflowDocument.Empty.Definition;
        var absent = new List<AbsentCallee>();
        var environment = await ResolveEnvironmentAsync(call, definition, current.Shapes, null, null, absent, cancellationToken)
            .ConfigureAwait(false);
        var options = await OptionsWithHelperChecksAsync(definition, current.Options, cancellationToken).ConfigureAwait(false);

        var result = CompileStored(
            document, unreadable, inputParametersJson, outputParametersJson, current.Shapes, options, environment, cancellationToken);
        if (result.Fault is { } fault)
        {
            var diagnostic = result.Diagnostics.Single(d => d.RuleId == CompilationRules.CompilationFault);
            logger.LogError(fault, "Compiling an imported document failed, and no answer exists: {Diagnostic}", diagnostic);
            throw new CompilationFaultException(diagnostic, fault);
        }

        var diagnostics = result.Diagnostics
            .Where(d => !(d.RuleId == CompilationRules.NodePortsUnresolved && absent.Any(a => a.Matches(d))))
            .ToList();
        return new ImportCompilation(diagnostics, [.. absent.Select(a => a.WorkflowId).Distinct()]);
    }

    /// <summary>What the binding pass makes of a draft: the diagnostics a save would report for it, and for each node the pass typed
    /// the term every port declares and the term it ends with. Nothing is memoised or saved.</summary>
    /// <remarks>The read, the environment and the compile are those of <see cref="EnsureAsync"/> for a version with no pin
    /// manifest, which is how a save and a publish compile. A definition the reader refuses answers <c>definition-unreadable</c>
    /// and no nodes; parameter lists that do not read answer their diagnostics and no nodes; a compile that stops
    /// at a diagnostic still answers every node the pass reached before it. The host's node validator is not asked.</remarks>
    /// <param name="definitionJson">The definition text; absent or blank is a definition the reader refuses.</param>
    /// <param name="inputParametersJson">The stored input parameter list; null is none.</param>
    /// <param name="outputParametersJson">The stored output parameter list; null is none.</param>
    /// <param name="cancellationToken">Handed to every I/O step and to the compile.</param>
    /// <exception cref="CompilationFaultException">An exception escaped a compiler phase; no answer exists.</exception>
    public async Task<DraftAnalysis> AnalyzeAsync(
        string? definitionJson, string? inputParametersJson, string? outputParametersJson, CancellationToken cancellationToken)
    {
        using var call = new CallScope(scopes);
        var current = CurrentSnapshot();
        var document = StoredWorkflowDocument.Read(definitionJson, out var unreadable);
        var definition = document?.Definition ?? StoredWorkflowDocument.Empty.Definition;
        var environment = await ResolveEnvironmentAsync(call, definition, current.Shapes, null, null, null, cancellationToken)
            .ConfigureAwait(false);
        var options = await OptionsWithHelperChecksAsync(definition, current.Options, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<NodeAnalysis> nodes = [];
        var result = CompileStored(
            document, unreadable, inputParametersJson, outputParametersJson, current.Shapes, options, environment, cancellationToken,
            context => nodes = DraftAnalyzer.Nodes(context));
        if (result.Fault is { } fault)
        {
            var diagnostic = result.Diagnostics.Single(d => d.RuleId == CompilationRules.CompilationFault);
            logger.LogError(fault, "Analysing a draft failed, and no answer exists: {Diagnostic}", diagnostic);
            throw new CompilationFaultException(diagnostic, fault);
        }

        return new DraftAnalysis(current.Options.Vocabulary.Generation, result.Diagnostics, nodes);
    }

    private sealed record AbsentCallee(CanvasPath Path, string NodeId, Guid WorkflowId)
    {
        public bool Matches(CompilationDiagnostic d) =>
            d.NodeId == NodeId && d.CanvasPath.SequenceEqual(Path.Segments);
    }

    private async Task<CompilationVerdict?> FindStoredAsync(
        IWorkflowCompilationStore? verdictStore, Guid versionId, string definitionDigest, string dependencyHash,
        CancellationToken cancellationToken)
    {
        if (verdictStore is null)
            return null;

        try
        {
            return await verdictStore.FindAsync(versionId, definitionDigest, dependencyHash, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CompilationStoreUnavailableException ex)
        {
            ReportUnavailable(ex);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Reading the compilation verdict for workflow version {WorkflowVersionId} failed; compiling instead.",
                versionId);
            return null;
        }
    }

    private void ReportUnavailable(CompilationStoreUnavailableException declined)
    {
        if (Interlocked.Exchange(ref storeUnavailableLogged, 1) == 0)
            logger.LogWarning(
                "The compilation verdict store declined a call: {Reason} Verdicts are kept in this process only. "
                + "This is logged once.", declined.Message);
    }

    private async Task<bool> SaveAsync(
        IWorkflowCompilationStore? verdictStore, CompilationVerdict verdict, CancellationToken cancellationToken)
    {
        if (verdictStore is null)
            return true;

        try
        {
            await verdictStore.SaveAsync(verdict, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CompilationStoreUnavailableException ex)
        {
            ReportUnavailable(ex);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Saving the compilation verdict for workflow version {WorkflowVersionId} failed; the verdict is "
                + "still returned, but the next call will save it again.",
                verdict.WorkflowVersionId);
            return false;
        }
    }

    private (CompilationVerdict Verdict, WorkflowProgram? Program) Compile(
        Guid versionId, string? inputParametersJson, string? outputParametersJson, StoredWorkflowDocument? document,
        StoredDocumentFault? unreadable, IBlockShapeSource shapes, CompilationOptions compileOptions, NodePortEnvironment environment,
        string definitionDigest, string dependencyHash, CancellationToken cancellationToken)
    {
        var result = CompileStored(
            document, unreadable, inputParametersJson, outputParametersJson, shapes, compileOptions, environment, cancellationToken);

        if (result.Fault is { } fault)
        {
            var diagnostic = result.Diagnostics.Single(d => d.RuleId == CompilationRules.CompilationFault);
            logger.LogError(fault,
                "Compiling workflow version {WorkflowVersionId} failed, and no verdict was recorded: {Diagnostic}",
                versionId, diagnostic.Message);
            throw new CompilationFaultException(diagnostic, fault);
        }
        var blockSources = result.BoundShapes.Values
            .Select(s => s.Source)
            .Where(s => s is not null)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        return (new CompilationVerdict(
            versionId, definitionDigest, dependencyHash, result.Succeeded,
            result.Diagnostics, blockSources!, DependencyFingerprint.CompilerVersion, time.GetUtcNow()), result.Program);
    }

    internal static CompilationResult CompileStored(
        StoredWorkflowDocument? document, StoredDocumentFault? unreadable, string? inputParametersJson, string? outputParametersJson,
        IBlockShapeSource shapes, CompilationOptions? options, NodePortEnvironment? environment, CancellationToken cancellationToken,
        Action<CompilationContext>? inspect = null)
    {
        var signatureRead = CanvasSignature.TryRead(inputParametersJson, outputParametersJson, out var signature, out var diagnostics);
        if (document is null)
            return CompilationResult.Refused([StoredWorkflowDocument.Unreadable(unreadable!), .. diagnostics]);
        if (!signatureRead)
            return CompilationResult.Refused(diagnostics);

        return WorkflowCompiler.Compile(document, shapes, signature!, options, environment, cancellationToken: cancellationToken, inspect: inspect);
    }

    /// <summary>The definition digest of a version already in hand — for a caller (a test, a report) that
    /// wants to compare a version's own digest without going through <see cref="EnsureAsync"/>.</summary>
    public static string ComputeDefinitionDigest(WorkflowVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return ComputeDefinitionDigest(version.DefinitionJson, version.InputParametersJson, version.OutputParametersJson);
    }

    internal static string ComputeDefinitionDigest(string definitionJson, string? inputParametersJson, string? outputParametersJson)
    {
        var builder = new StringBuilder();
        builder.Text(definitionJson).Text(inputParametersJson).Text(outputParametersJson);
        return DependencyFingerprint.HashHex(builder);
    }

    private async Task<NodePortEnvironment> ResolveEnvironmentAsync(
        CallScope call, WorkflowDefinition definition, IBlockShapeSource shapes,
        IReadOnlyDictionary<Guid, Guid>? pinManifest, IReadOnlyDictionary<Guid, WorkflowVersion>? pinnedVersions,
        List<AbsentCallee>? absent, CancellationToken cancellationToken)
    {
        var builder = new NodePortEnvironmentBuilder();
        var childCache = new Dictionary<Guid, Task<WorkflowVersion?>>();

        foreach (var frame in CanvasWalk.Frames(definition))
        {
            foreach (var node in frame.Canvas.Nodes)
            {
                if (shapes.Find(node.BlockTypeId) is not { Flags.PortsAreResolvedElsewhere: true } shape)
                    continue;

                var (resolution, absentCallee) = await ResolveNodeAsync(
                        call, frame, node, shape, pinManifest, pinnedVersions, childCache, cancellationToken)
                    .ConfigureAwait(false);
                if (absentCallee is { } callee)
                    absent?.Add(new AbsentCallee(frame.Path, node.Id, callee));
                if (resolution is { } filed)
                    builder.Add(frame.Path, node.Id, filed);
            }
        }

        return builder.Build();
    }

    private Task<CompilationOptions> OptionsWithHelperChecksAsync(
        WorkflowDefinition definition, CompilationOptions options, CancellationToken cancellationToken)
    {
        var paths = HelperPathsInDocument.Collect(definition, options.KeyShapes);
        return paths.Count == 0
            ? Task.FromResult(options.WithHelperChecks(new Dictionary<HelperPath, NamespacePathCheckAnswer>()))
            : OptionsWithHelperChecksAsync(definition, paths, options, cancellationToken);
    }

    private async Task<CompilationOptions> OptionsWithHelperChecksAsync(
        WorkflowDefinition definition, IReadOnlyCollection<HelperPath> paths, CompilationOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var answers = await options.HelperResolver.CheckAsync(paths, cancellationToken).ConfigureAwait(false);
            return options.WithHelperChecks(paths.ToDictionary(
                path => path, path => answers.TryGetValue(path, out var answer) ? answer : NamespacePathCheckAnswer.No));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            (CanvasPath Path, WorkflowNodeDefinition Node)? source = null;
            foreach (var frame in CanvasWalk.Frames(definition))
            {
                var node = frame.Canvas.Nodes.FirstOrDefault(n => WorkflowCompiler.ExpressionSources(frame.Canvas, n)
                    .Any(expression => HelperPathsInDocument.CollectExpressions([expression.Source], options.KeyShapes).Count > 0));
                if (node is not null) { source = (frame.Path, node); break; }
            }
            var diagnostic = new CompilationDiagnostic(CompilationRules.CompilationFault,
                $"The compiler could not finish its 'expression resolution' phase{(source is { } at ? $" at node '{at.Node.Id}'" : "")}: {ex.GetType().Name}: {ex.Message}",
                source?.Node.Id, CanvasPath: source?.Path.Segments ?? []);
            logger.LogError(ex, "Checking helper paths failed during compilation at node {NodeId}: {Message}", source?.Node.Id, diagnostic.Message);
            throw new CompilationFaultException(diagnostic, ex);
        }
    }

    private async Task<(NodePortResolution? Resolution, Guid? AbsentCallee)> ResolveNodeAsync(
        CallScope call, CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape,
        IReadOnlyDictionary<Guid, Guid>? pinManifest, IReadOnlyDictionary<Guid, WorkflowVersion>? pinnedVersions,
        Dictionary<Guid, Task<WorkflowVersion?>> childCache, CancellationToken cancellationToken)
    {
        string? effective = null;
        if (shape.RunsWorkflowFrom is { } referenceInput)
        {
            effective = EffectivePorts.IsReferenceInputMapped(frame.Canvas, node.Id, referenceInput)
                ? null
                : EffectivePorts.EffectiveReference(node, referenceInput);
            if (effective is null or "")
                return (null, null);

            if (string.Equals(node.BlockTypeId, WorkflowGraphHelpers.SubWorkflowBlockTypeId, StringComparison.OrdinalIgnoreCase))
                return await ResolveSubWorkflowAsync(call, effective, pinManifest, pinnedVersions, childCache, cancellationToken)
                    .ConfigureAwait(false);
        }

        if (!call.Inputs.Resolvers.TryGetValue(node.BlockTypeId, out var resolver))
            return (null, null);

        var request = new NodePortResolutionRequest(frame.Path, node, shape, effective);
        return (await resolver.ResolveAsync(request, cancellationToken).ConfigureAwait(false), null);
    }

    private static async Task<(NodePortResolution? Resolution, Guid? AbsentCallee)> ResolveSubWorkflowAsync(
        CallScope call, string reference, IReadOnlyDictionary<Guid, Guid>? pinManifest,
        IReadOnlyDictionary<Guid, WorkflowVersion>? pinnedVersions, Dictionary<Guid, Task<WorkflowVersion?>> childCache,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(reference, out var childId))
            return (NodePortResolution.Failed($"'{reference}' is not a workflow id."), null);

        if (pinManifest is not null && !pinManifest.ContainsKey(childId))
            return (NodePortResolution.Failed($"Workflow '{childId}' is not in the pinned version manifest."), null);

        if (call.ChildParameters is { } children)
        {
            return children.TryGetValue(childId, out var texts)
                ? (FromChildParameters(reference, childId, texts.Inputs, texts.Outputs), null)
                : (NodePortResolution.Failed($"Workflow '{childId}' has no version to read parameters from."), childId);
        }

        if (!childCache.TryGetValue(childId, out var pending))
        {
            pending = LoadChildVersionAsync(call, childId, pinManifest, pinnedVersions, cancellationToken);
            childCache[childId] = pending;
        }

        var childVersion = await pending.ConfigureAwait(false);
        if (childVersion is null)
            return (NodePortResolution.Failed($"Workflow '{childId}' has no version to read parameters from."), childId);

        return (FromChildParameters(reference, childId, childVersion.InputParametersJson, childVersion.OutputParametersJson), null);
    }

    private static NodePortResolution FromChildParameters(string reference, Guid childId, string? inputs, string? outputs)
    {
        if (CanvasSignature.TryRead(inputs, outputs, out var signature, out var diagnostics) && signature is not null)
            return NodePortResolution.Resolved(reference, signature);

        return NodePortResolution.Failed($"Workflow '{childId}' has declared parameters that could not be read: {diagnostics[0].Message}");
    }

    private static async Task<WorkflowVersion?> LoadChildVersionAsync(
        CallScope call, Guid childId, IReadOnlyDictionary<Guid, Guid>? pinManifest,
        IReadOnlyDictionary<Guid, WorkflowVersion>? pinnedVersions, CancellationToken cancellationToken)
    {
        if (pinManifest is not null && pinManifest.TryGetValue(childId, out var pinnedVersionId))
        {
            if (pinnedVersions is not null && pinnedVersions.TryGetValue(pinnedVersionId, out var read))
                return read;
            return await call.Inputs.Workflows.LoadVersionAsync(pinnedVersionId, cancellationToken).ConfigureAwait(false);
        }

        var child = await call.Inputs.Workflows.LoadAsync(childId, cancellationToken).ConfigureAwait(false);
        return child?.CurrentVersion;
    }

    private static IReadOnlyDictionary<string, WorkflowNodeDefinition> NodesById(WorkflowDefinition canvas)
    {
        var byId = new Dictionary<string, WorkflowNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in canvas.Nodes)
            byId.TryAdd(node.Id, node);

        return byId;
    }

    private static IEnumerable<INodePortResolver>? ResolversIn(IWorkflowRunScope scope) =>
        scope.Services.GetService(typeof(IEnumerable<INodePortResolver>)) as IEnumerable<INodePortResolver>;

    private static IReadOnlyDictionary<string, INodePortResolver> BuildResolverIndex(IEnumerable<INodePortResolver>? resolvers)
    {
        var byId = new Dictionary<string, INodePortResolver>(StringComparer.OrdinalIgnoreCase);
        foreach (var resolver in resolvers ?? [])
        {
            ArgumentNullException.ThrowIfNull(resolver);

            if (string.Equals(resolver.BlockTypeId, WorkflowGraphHelpers.SubWorkflowBlockTypeId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"A resolver was registered for block type '{WorkflowGraphHelpers.SubWorkflowBlockTypeId}', which "
                    + "the library already answers for.", nameof(resolvers));
            }

            if (!byId.TryAdd(resolver.BlockTypeId, resolver))
            {
                throw new ArgumentException(
                    $"Two resolvers are registered for block type id '{resolver.BlockTypeId}'.", nameof(resolvers));
            }
        }

        return byId;
    }

    private GenerationSnapshot CurrentSnapshot()
    {
        while (true)
        {
            var live = Volatile.Read(ref snapshot);
            if (string.Equals(live.Generation, GenerationKey(blocks.Generation, options.Vocabulary), StringComparison.Ordinal))
                return live;

            var fresh = NewSnapshot();
            if (ReferenceEquals(Interlocked.CompareExchange(ref snapshot, fresh, live), live))
                return fresh;
        }
    }

    private static string GenerationKey(string blocksGeneration, WorkflowTypeVocabulary vocabulary) =>
        $"{blocksGeneration}|{vocabulary.Generation}";

    private GenerationSnapshot NewSnapshot()
    {
        var vocabulary = options.Vocabulary;
        return new(
            GenerationKey(blocks.Generation, vocabulary), new CatalogueBlockShapeSource(blocks), new VerdictMemo(MemoCapacity), new(),
            options.PinnedTo(vocabulary), vocabulary.Fingerprint());
    }

    private readonly record struct CallInputs(
        IWorkflowStore Workflows,
        IWorkflowCompilationStore? Verdicts,
        IReadOnlyDictionary<string, INodePortResolver> Resolvers);

    private sealed class CallScope(IWorkflowRunScopeFactory factory) : IDisposable
    {
        private IWorkflowRunScope? scope;
        private CallInputs inputs;

        public CallInputs Inputs => scope is null ? Open() : inputs;

        public IReadOnlyDictionary<Guid, (string? Inputs, string? Outputs)>? ChildParameters { get; init; }

        public void Dispose() => scope?.Dispose();

        private CallInputs Open()
        {
            var opened = factory.CreateScope();
            try
            {
                inputs = new CallInputs(
                    opened.Store, opened.Services.GetService(typeof(IWorkflowCompilationStore)) as IWorkflowCompilationStore,
                    BuildResolverIndex(ResolversIn(opened)));
            }
            catch
            {
                opened.Dispose();
                throw;
            }

            scope = opened;
            return inputs;
        }
    }

    private sealed record GenerationSnapshot(
        string Generation,
        IBlockShapeSource Shapes,
        VerdictMemo Memo,
        ConcurrentDictionary<BlockShape, string> ShapeRenders,
        CompilationOptions Options,
        string RenderedVocabulary);
}
