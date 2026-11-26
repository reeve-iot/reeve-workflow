using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Http;

namespace ReeveWorkflow.AspNetCore;

/// <summary>The block catalogue, mounted separately from the workflow group. Its URL is a sibling rather
/// than a child, and a host gates it differently: reading the palette is not editing an automation.</summary>
public static class WorkflowBlockApiEndpoints
{
    /// <summary>Mounts the catalogue under one prefix and hands the group back, so the caller requires its
    /// own policy. Nothing here authorizes, and a mapping that declares neither <c>.RequireAuthorization(...)</c> nor
    /// <c>.AllowAnonymous()</c> on the returned group fails when the host builds its endpoints.</summary>
    public static RouteGroupBuilder MapWorkflowBlockApi(this IEndpointRouteBuilder endpoints, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var root = '/' + prefix.Trim('/');

        var group = endpoints.MapGroup(string.Empty);

        group.MapGet(root, GetBlockTypes)
            .Produces<IReadOnlyCollection<WorkflowBlockTypeResponse>>();

        AuthorizationIntent.Require(endpoints, group, "workflow block API", nameof(MapWorkflowBlockApi));

        return group;
    }

    private static Ok<IReadOnlyCollection<WorkflowBlockTypeResponse>> GetBlockTypes(
        [FromServices] IWorkflowBlocks blocks) =>
        TypedResults.Ok(WorkflowBlockDescription.Describe(blocks.GetAll()));
}
