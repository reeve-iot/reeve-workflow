using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Http;

namespace ReeveWorkflow.AspNetCore;

/// <summary>Expression preview, mounted separately from the workflow group: its URL is a sibling of
/// <c>api/workflows</c> rather than a child, and a host gates reading a preview differently from editing.</summary>
public static class WorkflowExpressionApiEndpoints
{
    /// <summary>Mounts the preview under one prefix and hands the group back. The only refusal here is the
    /// language's own; the host states its policy on the group this returns, and a mapping that declares neither
    /// <c>.RequireAuthorization(...)</c> nor <c>.AllowAnonymous()</c> fails when the host builds its endpoints.</summary>
    public static RouteGroupBuilder MapWorkflowExpressionApi(this IEndpointRouteBuilder endpoints, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var root = '/' + prefix.Trim('/');

        var group = endpoints.MapGroup(string.Empty);

        group.MapPost($"{root}/preview", PreviewExpression)
            .Accepts<ExpressionPreviewRequest>("application/json")
            .Produces<ExpressionPreviewResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        AuthorizationIntent.Require(endpoints, group, "workflow expression API", nameof(MapWorkflowExpressionApi));

        return group;
    }

    private static async Task<IResult> PreviewExpression(
        HttpContext httpContext,
        [FromServices] IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var (request, refusal) = await JsonRequestBody.ReadAsync<ExpressionPreviewRequest>(
            httpContext, optional: false,
            detail => TypedResults.Problem(detail, statusCode: StatusCodes.Status400BadRequest), cancellationToken);
        if (request is null)
            return refusal!;

        var previews = services.GetService<ExpressionPreviewService>()
            ?? services.GetKeyedService<ExpressionPreviewService>(ExpressionPreviewService.LibraryServiceKey);

        if (previews is null)
            return TypedResults.Problem(
                "No ExpressionPreviewService is registered. Call AddWorkflows, or register one of your own.",
                statusCode: StatusCodes.Status500InternalServerError);

        if (request.Expression is null)
            return TypedResults.Problem("Expression body is required", statusCode: StatusCodes.Status400BadRequest);

        if (request.Expression.Length > ExpressionLimits.MaxExpressionLength)
            return TypedResults.Problem(
                $"Expression exceeds the maximum length of {ExpressionLimits.MaxExpressionLength} characters",
                statusCode: StatusCodes.Status400BadRequest);

        if (request.ContextScope?.Length > ExpressionLimits.MaxContextScopeLength)
            return TypedResults.Problem(
                $"Context scope exceeds the maximum length of {ExpressionLimits.MaxContextScopeLength} characters",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await previews.PreviewAsync(request.Expression, request.ContextScope ?? "global", services, cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new ExpressionPreviewResponse(
            Ok: result.Ok,
            Value: result.Value,
            Type: result.Type,
            Error: result.Error is null
                ? null
                : new ExpressionPreviewErrorDto(
                    result.Error.Message, result.Error.Position, result.Error.Suggestion, result.Error.RuleId),
            ElapsedMicroseconds: result.ElapsedMicroseconds));
    }
}
