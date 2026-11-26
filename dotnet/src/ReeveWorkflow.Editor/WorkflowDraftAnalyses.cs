using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor;

internal sealed class WorkflowDraftAnalyses(
    WorkflowDbContext context, WorkflowCompilationService compiler, ILogger<WorkflowDraftAnalyses> logger)
{
    public async Task<DraftAnalysisResponse> AnalyzeAsync(Guid userId, DraftAnalysisRequest request, CancellationToken cancellationToken)
    {
        if (request.WorkflowId is { } id
            && !await context.Workflows.AsNoTracking().AnyAsync(w => w.Id == id && w.CreatedByUserId == userId, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowNotFoundException(id);
        }

        try
        {
            var analysis = await compiler
                .AnalyzeAsync(request.DefinitionJson, request.InputParametersJson, request.OutputParametersJson, cancellationToken)
                .ConfigureAwait(false);
            return new DraftAnalysisResponse(request.Revision, analysis.RegistryGeneration, analysis.Diagnostics, analysis.Nodes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fault)
        {
            logger.LogWarning(fault, "Whether a draft of workflow {WorkflowId} compiles could not be answered.", request.WorkflowId);
            throw new WorkflowCompilationUnansweredException(request.WorkflowId ?? Guid.Empty, Guid.Empty,
                "Whether the draft compiles could not be answered. Asking again may succeed.", fault);
        }
    }
}
