using System.Net.Mime;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Http;

namespace ReeveWorkflow.Editor;

/// <summary>The write half of the workflow HTTP surface, mounted and gated as one group. It shares the
/// Executor's prefix by design: to a caller there is one collection, written and read at one address.</summary>
public static class WorkflowEditorApiEndpoints
{
    private const string RetryAfterSeconds = "5";

    /// <summary>Mounts create, update, delete, publish and rollback, the two compilation reads, the port-type
    /// vocabulary and the draft analysis, under one prefix. The group is handed back so the caller states its policy on it, and mounting it
    /// without one is refused.</summary>
    /// <remarks>A save stored as the draft because it does not compile answers with the workflow alone, its
    /// <c>DraftVersionId</c> set; <c>GET {prefix}/{versionId}/compilation</c> gives that draft's diagnostics. Mounting
    /// with no policy is refused rather than left open: an unauthenticated write surface is the one default whose cost
    /// is not paid by whoever forgot it. Say <c>.AllowAnonymous()</c> to mean it.</remarks>
    public static RouteGroupBuilder MapWorkflowEditorApi(this IEndpointRouteBuilder endpoints, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var root = '/' + prefix.Trim('/');

        var group = endpoints.MapGroup(string.Empty);

        group.MapPost(root, CreateWorkflowAsync)
            .Accepts<CreateWorkflowRequest>(MediaTypeNames.Application.Json)
            .Produces<WorkflowResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces<WorkflowSaveUnpublishedProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        group.MapPut($"{root}/{{id:guid}}", UpdateWorkflowAsync)
            .Accepts<UpdateWorkflowRequest>(MediaTypeNames.Application.Json)
            .Produces<WorkflowResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces<WorkflowSaveUnpublishedProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete($"{root}/{{id:guid}}", DeleteWorkflowAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost($"{root}/{{id:guid}}/publish", PublishWorkflowAsync)
            .Produces<WorkflowResponse>()
            .Produces<CompilationProblemDetails>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost($"{root}/{{id:guid}}/versions/{{versionNumber:int}}/rollback", RollbackWorkflowAsync)
            .Produces<WorkflowResponse>()
            .Produces<CompilationProblemDetails>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet($"{root}/compilation/invalid", GetInvalidCompilationsAsync)
            .Produces<CompilationReportResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet($"{root}/{{versionId:guid}}/compilation", GetVersionCompilationAsync)
            .Produces<WorkflowCompilationResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet($"{root}/port-types", GetPortTypes)
            .Produces<PortTypeVocabularyResponse>();

        group.MapPost($"{root}/analysis", AnalyzeDraftAsync)
            .Accepts<DraftAnalysisRequest>(MediaTypeNames.Application.Json)
            .Produces<DraftAnalysisResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        AuthorizationIntent.Require(endpoints, group, "workflow editor API", nameof(MapWorkflowEditorApi));

        return group;
    }

    private static async Task<IResult> CreateWorkflowAsync(
        ClaimsPrincipal user, [FromServices] IWorkflowEditor editor, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        var (request, refusal) = await JsonRequestBody.ReadAsync<CreateWorkflowRequest>(
            context, optional: false, detail => Problem(StatusCodes.Status400BadRequest, detail), cancellationToken);
        if (request is null)
            return refusal!;

        if (request.Origin is { } origin && origin != WorkflowDefinitionOrigin.Edited)
        {
            return Problem(StatusCodes.Status400BadRequest,
                $"A create over HTTP accepts origin 'Edited' only, not '{origin}': the secrets an import drops are reported to an in-process caller.",
                WorkflowSaveRefusalCodes.OriginNotAccepted);
        }

        try
        {
            var result = await editor.CreateAsync(userId, request, cancellationToken).ConfigureAwait(false);
            return PublishUnfinished(result, publishAsked: true, context)
                ?? TypedResults.Created(LocationOf(context, result.Workflow.Id), result.Workflow);
        }
        catch (Exception exception) when (IsBadRequest(exception))
        {
            return Problem(StatusCodes.Status400BadRequest, exception.Message, (exception as InvalidWorkflowEditRequestException)?.Code);
        }
        catch (Exception exception) when (exception is WorkflowVersionConflictException or WorkflowDraftChangedException)
        {
            return Problem(StatusCodes.Status409Conflict, exception.Message);
        }
    }

    private static string LocationOf(HttpContext context, Guid workflowId)
    {
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var path = string.IsNullOrEmpty(target)
            ? context.Request.PathBase.ToUriComponent() + context.Request.Path.ToUriComponent()
            : target.Split('?', 2)[0];

        return $"{path.TrimEnd('/')}/{workflowId}";
    }

    private static async Task<IResult> UpdateWorkflowAsync(
        Guid id, ClaimsPrincipal user,
        [FromServices] IWorkflowEditor editor, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        var (request, refusal) = await JsonRequestBody.ReadAsync<UpdateWorkflowRequest>(
            context, optional: false, detail => Problem(StatusCodes.Status400BadRequest, detail), cancellationToken);
        if (request is null)
            return refusal!;

        try
        {
            var result = await editor.UpdateAsync(userId, id, request, cancellationToken).ConfigureAwait(false);
            return PublishUnfinished(result, request.AutoPublish, context) ?? TypedResults.Ok(result.Workflow);
        }
        catch (WorkflowNotFoundException)
        {
            return WorkflowNotFound();
        }
        catch (Exception exception) when (IsBadRequest(exception))
        {
            return Problem(StatusCodes.Status400BadRequest, exception.Message, (exception as InvalidWorkflowEditRequestException)?.Code);
        }
        catch (WorkflowRevisionMismatchException exception)
        {
            return Problem(StatusCodes.Status409Conflict, exception.Message, WorkflowEditConflictCodes.RevisionMismatch);
        }
        catch (Exception exception) when (exception is WorkflowVersionConflictException or WorkflowDraftChangedException)
        {
            return Problem(StatusCodes.Status409Conflict, exception.Message);
        }
    }

    private static async Task<IResult> DeleteWorkflowAsync(
        Guid id, ClaimsPrincipal user, [FromServices] IWorkflowEditor editor, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        try
        {
            await editor.DeleteAsync(userId, id, cancellationToken).ConfigureAwait(false);
            return TypedResults.NoContent();
        }
        catch (WorkflowNotFoundException)
        {
            return WorkflowNotFound();
        }
        catch (WorkflowHasActiveExecutionsException exception)
        {
            return Problem(StatusCodes.Status409Conflict, exception.Message);
        }
    }

    private static async Task<IResult> PublishWorkflowAsync(
        Guid id, ClaimsPrincipal user, [FromServices] IWorkflowEditor editor, HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        try
        {
            var result = await editor.PublishAsync(userId, id, cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(result.Workflow);
        }
        catch (WorkflowNotFoundException)
        {
            return WorkflowNotFound();
        }
        catch (NoDraftVersionToPublishException exception)
        {
            return Problem(StatusCodes.Status409Conflict, exception.Message);
        }
        catch (Exception exception)
        {
            if (PromoteRefusal(exception, context) is { } refusal)
                return refusal;
            throw;
        }
    }

    private static async Task<IResult> RollbackWorkflowAsync(
        Guid id, int versionNumber, ClaimsPrincipal user,
        [FromServices] IWorkflowEditor editor, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        try
        {
            var result = await editor.RollbackAsync(userId, id, versionNumber, cancellationToken).ConfigureAwait(false);
            if (!result.VersionPublished && result.Diagnostics.Count > 0)
            {
                var refused = new WorkflowDoesNotCompileException(result.Diagnostics);
                return BadRequest(refused.Message, refused.Diagnostics);
            }

            return TypedResults.Ok(result.Workflow);
        }
        catch (Exception exception) when (
            exception is WorkflowNotFoundException or WorkflowVersionNotFoundException)
        {
            return WorkflowNotFound();
        }
        catch (CannotRollbackToActiveVersionException exception)
        {
            return Problem(StatusCodes.Status409Conflict, exception.Message);
        }
        catch (Exception exception)
        {
            if (PromoteRefusal(exception, context) is { } refusal)
                return refusal;
            throw;
        }
    }

    private static async Task<IResult> GetInvalidCompilationsAsync(
        ClaimsPrincipal user, [FromServices] WorkflowCompilationReads reads, CancellationToken cancellationToken,
        int skip = 0, int take = 20)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        return TypedResults.Ok(await reads.InvalidAsync(userId, skip, take, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> GetVersionCompilationAsync(
        Guid versionId, ClaimsPrincipal user, [FromServices] WorkflowCompilationReads reads, HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        try
        {
            return await reads.VerdictAsync(userId, versionId, cancellationToken).ConfigureAwait(false) is { } verdict
                ? TypedResults.Ok(verdict)
                : Problem(StatusCodes.Status404NotFound, "Workflow version not found");
        }
        catch (WorkflowCompilationUnansweredException exception)
        {
            return Unanswered(exception, context);
        }
    }

    private static Ok<PortTypeVocabularyResponse> GetPortTypes([FromServices] CompilationOptions options, [FromServices] IServiceProvider services) =>
        TypedResults.Ok(PortTypeVocabularyResponse.From(options.Vocabulary, services));

    private static async Task<IResult> AnalyzeDraftAsync(
        ClaimsPrincipal user, [FromServices] WorkflowDraftAnalyses analyses, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        var (request, refusal) = await JsonRequestBody.ReadAsync<DraftAnalysisRequest>(
            context, optional: false, detail => Problem(StatusCodes.Status400BadRequest, detail), cancellationToken);
        if (request is null)
            return refusal!;

        try
        {
            return TypedResults.Ok(await analyses.AnalyzeAsync(userId, request, cancellationToken).ConfigureAwait(false));
        }
        catch (WorkflowNotFoundException)
        {
            return WorkflowNotFound();
        }
        catch (WorkflowCompilationUnansweredException exception)
        {
            return Unanswered(exception, context);
        }
    }

    private static IResult? PromoteRefusal(Exception exception, HttpContext context) => exception switch
    {
        WorkflowDoesNotCompileException refused => BadRequest(refused.Message, refused.Diagnostics),
        _ when IsBadRequest(exception) => BadRequest(exception.Message, [], (exception as InvalidWorkflowEditRequestException)?.Code),
        WorkflowVersionConflictException or WorkflowDraftChangedException => Problem(StatusCodes.Status409Conflict, exception.Message),
        WorkflowCompilationUnansweredException unanswered => Unanswered(unanswered, context),
        WorkflowPublishInHostTransactionException => Problem(StatusCodes.Status500InternalServerError, exception.Message),
        _ => null,
    };

    private static IResult? PublishUnfinished(WorkflowEditResult result, bool publishAsked, HttpContext context)
    {
        if (!publishAsked || result.VersionPublished || result.Fault is null)
            return null;

        context.Response.Headers.RetryAfter = RetryAfterSeconds;
        var (type, title) = ProblemDefaults(StatusCodes.Status503ServiceUnavailable);
        return TypedResults.Json(
            new WorkflowSaveUnpublishedProblemDetails(
                type, title, StatusCodes.Status503ServiceUnavailable,
                $"The save is stored as the draft of workflow '{result.Workflow.Id}', and publishing it could not be "
                + "finished. Publish the draft again later.",
                result.Workflow, result.VersionId!.Value, result.VersionNumber!.Value),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult Unanswered(WorkflowCompilationUnansweredException exception, HttpContext context)
    {
        context.Response.Headers.RetryAfter = RetryAfterSeconds;
        return Problem(StatusCodes.Status503ServiceUnavailable, exception.Message);
    }

    private static bool IsBadRequest(Exception exception) => exception
        is InvalidWorkflowDefinitionException or InvalidWorkflowEditRequestException or ArgumentException;

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        userId = Guid.Empty;
        if (user.Identity?.IsAuthenticated != true)
            return false;

        var value = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(value, out userId) && userId != Guid.Empty;
    }

    private static IResult WorkflowNotFound() => Problem(StatusCodes.Status404NotFound, "Workflow not found");

    private static IResult Problem(int statusCode, string detail, string? code = null)
    {
        var problem = new ProblemDetails { Status = statusCode, Detail = ScrubParameterSuffix(detail) };
        if (code is not null)
            problem.Extensions[Core.Execution.WorkflowStartRefusedException.ProblemExtension] = code;
        return TypedResults.Json(problem, statusCode: statusCode);
    }

    private static IResult BadRequest(string detail, IReadOnlyList<CompilationDiagnostic> diagnostics, string? code = null)
    {
        var (type, title) = ProblemDefaults(StatusCodes.Status400BadRequest);
        return TypedResults.Json(
            new CompilationProblemDetails(type, title, StatusCodes.Status400BadRequest, ScrubParameterSuffix(detail), diagnostics, code),
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static (string Type, string Title) ProblemDefaults(int statusCode)
    {
        var defaults = TypedResults.Problem(statusCode: statusCode).ProblemDetails;
        return (defaults.Type!, defaults.Title!);
    }

    private static string ScrubParameterSuffix(string detail)
    {
        if (string.IsNullOrEmpty(detail))
            return detail;

        const string marker = " (Parameter '";
        var index = detail.LastIndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? detail[..index] : detail;
    }
}
