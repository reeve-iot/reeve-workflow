using System.Net.Mime;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Http;

namespace ReeveWorkflow.AspNetCore;

/// <summary>Route names for the two endpoints a host links back to when it creates a workflow. They are the
/// library's, so a host's 201 Location survives a change to the templates it does not own.</summary>
public static class WorkflowApiRouteNames
{
    /// <summary>GET the collection.</summary>
    public const string GetWorkflows = "ReeveWorkflow_GetWorkflows";

    /// <summary>GET one workflow by id.</summary>
    public const string GetWorkflow = "ReeveWorkflow_GetWorkflow";
}

/// <summary>The workflow HTTP surface, as one group the host mounts and gates: the library cannot know the
/// host's policy names, and mounting one endpoint at a time would let a host gate nine of ten.</summary>
public static class WorkflowApiEndpoints
{
    private const int StatusCodeClientClosedRequest = 499;

    private const string RetryAfterSeconds = "5";

    /// <summary>Mounts every workflow endpoint under one prefix and hands the group back so the caller can
    /// require its own policy. Nothing here authorizes, and a mapping that declares neither <c>.RequireAuthorization(...)</c>
    /// nor <c>.AllowAnonymous()</c> on the returned group fails when the host builds its endpoints.</summary>
    public static RouteGroupBuilder MapWorkflowApi(this IEndpointRouteBuilder endpoints, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var root = '/' + prefix.Trim('/');

        var group = endpoints.MapGroup(string.Empty);

        group.MapGet(root, GetWorkflowsAsync)
            .WithName(WorkflowApiRouteNames.GetWorkflows)
            .Produces<IReadOnlyCollection<WorkflowSummaryResponse>>();

        group.MapGet($"{root}/{{id:guid}}", GetWorkflowAsync)
            .WithName(WorkflowApiRouteNames.GetWorkflow)
            .Produces<WorkflowResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost($"{root}/{{id:guid}}/execute", ExecuteWorkflowAsync)
            .Accepts<ExecuteWorkflowRequest>(isOptional: true, MediaTypeNames.Application.Json)
            .Produces<IReadOnlyDictionary<string, object?>>()
            .Produces<CompilationProblemDetails>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<WorkflowConcurrencyConflictResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodeClientClosedRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet($"{root}/{{id:guid}}/executions", GetExecutionsAsync)
            .Produces<PagedWorkflowExecutionsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet($"{root}/{{id:guid}}/executions/{{executionId:guid}}", GetExecutionAsync)
            .Produces<WorkflowExecutionDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet($"{root}/{{id:guid}}/executions/{{executionId:guid}}/replay", GetExecutionReplayAsync)
            .Produces<IReadOnlyCollection<WorkflowExecutionStepResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet($"{root}/{{id:guid}}/versions", GetVersionsAsync)
            .Produces<IReadOnlyCollection<WorkflowVersionSummaryResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet($"{root}/{{id:guid}}/versions/{{versionNumber:int}}", GetVersionAsync)
            .Produces<WorkflowVersionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet($"{root}/{{id:guid}}/versions/{{versionA:int}}/diff/{{versionB:int}}", DiffVersionsAsync)
            .Produces<WorkflowDiffResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost($"{root}/import/validate", ValidateImportAsync)
            .Accepts<WorkflowExportDto>(MediaTypeNames.Application.Json)
            .Produces<WorkflowImportValidationResult>()
            .Produces<CompilationProblemDetails>(StatusCodes.Status400BadRequest);

        AuthorizationIntent.Require(endpoints, group, "workflow API", nameof(MapWorkflowApi));

        return group;
    }

    private static async Task<IResult> GetWorkflowsAsync(
        ClaimsPrincipal user, [FromServices] IWorkflowCatalog catalog, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        return TypedResults.Ok(await catalog.GetWorkflowsAsync(userId, cancellationToken));
    }

    private static async Task<IResult> GetWorkflowAsync(
        Guid id, ClaimsPrincipal user, [FromServices] IWorkflowCatalog catalog, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        try
        {
            return TypedResults.Ok(await catalog.GetWorkflowAsync(userId, id, cancellationToken));
        }
        catch (WorkflowNotFoundException)
        {
            return WorkflowNotFound();
        }
    }

    private static async Task<IResult> GetExecutionsAsync(
        Guid id,
        ClaimsPrincipal user,
        [FromServices] IWorkflowCatalog catalog,
        CancellationToken cancellationToken,
        bool? hasErrors = null,
        string? status = null,
        int skip = 0,
        int take = 50)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        try
        {
            return TypedResults.Ok(
                await catalog.GetExecutionsPagedAsync(userId, id, skip, take, hasErrors, status, cancellationToken));
        }
        catch (WorkflowNotFoundException)
        {
            return WorkflowNotFound();
        }
    }

    private static async Task<IResult> GetExecutionAsync(
        Guid id, Guid executionId, ClaimsPrincipal user, [FromServices] IWorkflowCatalog catalog, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        try
        {
            return TypedResults.Ok(await catalog.GetExecutionAsync(userId, id, executionId, cancellationToken));
        }
        catch (WorkflowNotFoundException)
        {
            return WorkflowNotFound();
        }
        catch (WorkflowExecutionNotFoundException)
        {
            return Problem(StatusCodes.Status404NotFound, "Execution not found");
        }
    }

    private static async Task<IResult> GetExecutionReplayAsync(
        Guid id, Guid executionId, ClaimsPrincipal user, [FromServices] IWorkflowCatalog catalog, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        try
        {
            return TypedResults.Ok(await catalog.GetExecutionReplayAsync(userId, id, executionId, cancellationToken));
        }
        catch (WorkflowNotFoundException)
        {
            return WorkflowNotFound();
        }
        catch (WorkflowExecutionNotFoundException)
        {
            return Problem(StatusCodes.Status404NotFound, "Execution not found");
        }
    }

    private static async Task<IResult> GetVersionsAsync(
        Guid id,
        ClaimsPrincipal user,
        [FromServices] IWorkflowCatalog catalog,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 20)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        try
        {
            return TypedResults.Ok(await catalog.GetWorkflowVersionsAsync(userId, id, page, pageSize, cancellationToken));
        }
        catch (WorkflowNotFoundException)
        {
            return WorkflowNotFound();
        }
    }

    private static async Task<IResult> GetVersionAsync(
        Guid id, int versionNumber, ClaimsPrincipal user, [FromServices] IWorkflowCatalog catalog, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        try
        {
            return TypedResults.Ok(await catalog.GetWorkflowVersionAsync(userId, id, versionNumber, cancellationToken));
        }
        catch (WorkflowNotFoundException)
        {
            return WorkflowNotFound();
        }
        catch (WorkflowVersionNotFoundException)
        {
            return Problem(StatusCodes.Status404NotFound, "Workflow version not found");
        }
    }

    private static async Task<IResult> DiffVersionsAsync(
        Guid id, int versionA, int versionB, ClaimsPrincipal user, [FromServices] IWorkflowCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        try
        {
            return TypedResults.Ok(await catalog.DiffVersionsAsync(userId, id, versionA, versionB, cancellationToken));
        }
        catch (WorkflowNotFoundException)
        {
            return WorkflowNotFound();
        }
        catch (WorkflowVersionNotFoundException)
        {
            return Problem(StatusCodes.Status404NotFound, "Workflow version not found");
        }
        catch (InvalidWorkflowDefinitionException exception)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, exception.Message);
        }
    }

    private static async Task<IResult> ValidateImportAsync(
        HttpContext httpContext, ClaimsPrincipal user, [FromServices] IWorkflowCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out _))
            return TypedResults.Unauthorized();

        var (dto, refusal) = await JsonRequestBody.ReadAsync<WorkflowExportDto>(
            httpContext, optional: false, detail => CompilationProblem(detail, []), cancellationToken);
        if (dto is null)
            return refusal!;

        if (WorkflowRowSettings.Refusal(dto.ExecutionTimeoutSeconds, dto.RetryPolicyJson) is { } rowRefusal)
            return CompilationProblem(rowRefusal, []);

        try
        {
            return TypedResults.Ok(await catalog.ValidateWorkflowImportAsync(
                dto.DefinitionJson, dto.InputParametersJson, dto.OutputParametersJson, cancellationToken));
        }
        catch (WorkflowDoesNotCompileException exception)
        {
            return CompilationProblem(exception.Message, exception.Diagnostics);
        }
    }

    private static async Task<IResult> ExecuteWorkflowAsync(
        Guid id,
        ClaimsPrincipal user,
        HttpContext httpContext,
        [FromServices] IWorkflowCatalog catalog,
        [FromServices] IWorkflowEngine engine,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
            return TypedResults.Unauthorized();

        var (request, refusal) = await JsonRequestBody.ReadAsync<ExecuteWorkflowRequest>(
            httpContext, optional: true, detail => CompilationProblem(detail, []), cancellationToken);
        if (refusal is not null)
            return refusal;

        try
        {
            await catalog.GetWorkflowAsync(userId, id, cancellationToken);

            using var observationScope = WorkflowRunObservationScope.Push(
                new WorkflowRunObservation { RecordLogEntries = request?.DebugMode ?? true });
            var outputs = await engine.ExecuteAsync(
                id,
                fire: null,
                inputParameters: request?.InputParameters,
                cancellationToken: cancellationToken);

            return TypedResults.Ok(outputs);
        }
        catch (WorkflowNotFoundException)
        {
            return WorkflowNotFound();
        }
        catch (WorkflowValidationException exception)
        {
            return CompilationProblem(ScrubParameterSuffix(exception.Message), []);
        }
        catch (WorkflowScriptingPermissionException exception)
        {
            return Problem(StatusCodes.Status403Forbidden, exception.Message);
        }
        catch (WorkflowStartRefusedException exception) when (exception.IsFault)
        {
            httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;
            return Problem(StatusCodes.Status503ServiceUnavailable, exception.Message);
        }
        catch (WorkflowStartRefusedException exception)
        {
            return TypedResults.Json(
                new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Detail = exception.Message,
                    Extensions = { [WorkflowStartRefusedException.ProblemExtension] = exception.Code }
                },
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (WorkflowDoesNotCompileException exception)
        {
            return CompilationProblem(exception.Message, exception.Diagnostics);
        }
        catch (WorkflowStartFindingsException exception)
        {
            return CompilationProblem(exception.Message, exception.Diagnostics);
        }
        catch (WorkflowCompilationUnansweredException exception)
        {
            httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;
            return Problem(StatusCodes.Status503ServiceUnavailable, exception.Message);
        }
        catch (Exception exception) when (
            exception is WorkflowNodeExecutionException ||
            exception is SubCanvasRethrowException)
        {
            return TypedResults.Ok((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>());
        }
        catch (WorkflowConcurrencyException exception)
        {
            return TypedResults.Json(
                new WorkflowConcurrencyConflictResponse(
                    exception.WorkflowId, exception.Mode, exception.Outcome, exception.Message),
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (WorkflowSupersededException) when (!cancellationToken.IsCancellationRequested)
        {
            return TypedResults.StatusCode(StatusCodeClientClosedRequest);
        }
    }

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(value, out userId);
    }

    private static IResult WorkflowNotFound() => Problem(StatusCodes.Status404NotFound, "Workflow not found");

    private static IResult Problem(int statusCode, string detail) =>
        TypedResults.Json(
            new ProblemDetails { Status = statusCode, Detail = ScrubParameterSuffix(detail) },
            statusCode: statusCode);

    private static IResult CompilationProblem(string detail, IReadOnlyList<CompilationDiagnostic> diagnostics)
    {
        var defaults = TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest).ProblemDetails;
        return TypedResults.Json(
            new CompilationProblemDetails(defaults.Type!, defaults.Title!, StatusCodes.Status400BadRequest, detail, diagnostics),
            statusCode: StatusCodes.Status400BadRequest);
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
