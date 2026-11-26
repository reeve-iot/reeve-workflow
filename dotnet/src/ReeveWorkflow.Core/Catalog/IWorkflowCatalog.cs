using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Catalog;

/// <summary>Every read a request can make of a workflow. Ownership is a parameter rather than an ambient
/// identity: the host decides who a caller is, and the library only refuses what that user does not own.</summary>
public interface IWorkflowCatalog
{
    /// <summary>Every workflow this user owns, with its run statistics.</summary>
    Task<IReadOnlyCollection<WorkflowSummaryResponse>> GetWorkflowsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>One workflow, or a refusal when the user does not own it.</summary>
    Task<WorkflowResponse> GetWorkflowAsync(
        Guid userId, Guid workflowId, CancellationToken cancellationToken = default);

    /// <summary>What would go wrong if this definition were imported — missing block types and workflows, and the ids it
    /// references — without importing it. It compiles, and answers every refusal one way: it throws
    /// <see cref="Compilation.WorkflowDoesNotCompileException"/>, carrying the diagnostics publish would answer, for a
    /// document the reader refuses, one that breaks a structural rule, or one that does not compile. A block type this host
    /// lacks, and a <c>SubWorkflow</c> callee it lacks, are reported in the result, never refused. The texts are judged as sent: a document that breaks a compile rule (an alias or <c>Any</c>/<c>Float</c> type name, a text
    /// literal on a <c>Boolean</c>, <c>Int</c> or <c>Number</c> site, a <c>Json</c> wire or <c>expr:</c> read of one) is
    /// refused with the diagnostics a publish would answer, and nothing is rewritten.</summary>
    /// <param name="definitionJson">The definition text. Null is accepted and refused as a document the reader cannot read
    /// (<c>definition-unreadable</c>), so an HTTP caller answers 400 for it.</param>
    /// <param name="inputParametersJson">The stored input parameter list the document is imported with; null is none. Pass what
    /// the import will store: a document that reads or wires a parameter it does not declare is refused with
    /// <c>wire-port-unknown</c>.</param>
    /// <param name="outputParametersJson">The stored output parameter list; null is none.</param>
    /// <param name="cancellationToken">Cancels the read and the compile.</param>
    Task<WorkflowImportValidationResult> ValidateWorkflowImportAsync(
        string? definitionJson, string? inputParametersJson, string? outputParametersJson,
        CancellationToken cancellationToken = default);

    /// <summary>This workflow's runs, unpaged.</summary>
    Task<IReadOnlyCollection<WorkflowExecutionResponse>> GetExecutionsAsync(
        Guid userId, Guid workflowId, bool? hasErrors = null, string? status = null,
        CancellationToken cancellationToken = default);

    /// <summary>One page of this workflow's runs, with the total the page came from.</summary>
    Task<PagedWorkflowExecutionsResponse> GetExecutionsPagedAsync(
        Guid userId, Guid workflowId, int skip = 0, int take = 50, bool? hasErrors = null,
        string? status = null, CancellationToken cancellationToken = default);

    /// <summary>One run, with its log entries.</summary>
    Task<WorkflowExecutionDetailResponse> GetExecutionAsync(
        Guid userId, Guid workflowId, Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>The steps one run took, in order, for the replay view.</summary>
    Task<IReadOnlyCollection<WorkflowExecutionStepResponse>> GetExecutionReplayAsync(
        Guid userId, Guid workflowId, Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>One page of this workflow's versions, newest first.</summary>
    Task<IReadOnlyCollection<WorkflowVersionSummaryResponse>> GetWorkflowVersionsAsync(
        Guid userId, Guid workflowId, int page = 1, int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>One version of this workflow, by its number.</summary>
    Task<WorkflowVersionResponse> GetWorkflowVersionAsync(
        Guid userId, Guid workflowId, int versionNumber, CancellationToken cancellationToken = default);

    /// <summary>What changed between two versions as a run reads them: nodes, edges and their order, the root's output
    /// mappings and both parameter lists. <see cref="WorkflowDiffResponse"/> names the few fields no reader of a run
    /// reads. A node's resilience is read with the host's blocks, as the engine builds it for that block.</summary>
    /// <exception cref="Execution.InvalidWorkflowDefinitionException">Either version's definition or a parameter list the stored
    /// document reader refuses, or two nodes of one id on one canvas.</exception>
    Task<WorkflowDiffResponse> DiffVersionsAsync(
        Guid userId, Guid workflowId, int versionA, int versionB, CancellationToken cancellationToken = default);
}
