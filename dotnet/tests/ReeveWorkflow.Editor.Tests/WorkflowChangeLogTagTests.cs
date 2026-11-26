using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Editor.Tests;

public sealed class WorkflowChangeLogTagTests
{
    private const string DefinitionA = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
    private const string DefinitionB = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"b","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"b","mappings":{}}]}""";
    private const string DefinitionC = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"c","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"c","mappings":{}}]}""";
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Every_bulk_editor_operation_passes_the_change_log_guard()
    {
        var guard = new WorkflowChangeLogGuard();
        using var db = new SqliteEditorDatabase(queryInterceptor: guard);

        var created = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, DefinitionA)));
        var id = created.Workflow.Id;
        await db.EditAsync(e => e.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, DefinitionB, AutoPublish: false)));
        await db.EditAsync(e => e.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, DefinitionC, AutoPublish: false)));
        await db.EditAsync(e => e.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, DefinitionA, AutoPublish: false)));
        await db.EditAsync(e => e.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, DefinitionB, AutoPublish: true)));
        await db.EditAsync(e => e.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, DefinitionC, AutoPublish: false)));
        await db.EditAsync(e => e.PublishAsync(Owner, id));
        await db.EditAsync(e => e.RollbackAsync(Owner, id, 1));
        await db.EditAsync(e => e.DeleteAsync(Owner, id));

        guard.Refused.Should().BeEmpty();
        guard.AcceptedBulkOperations.Should().Contain("ExecuteUpdate<Workflow>");
        guard.AcceptedBulkOperations.Should().Contain("ExecuteUpdate<WorkflowVersion>");
        guard.AcceptedBulkOperations.Should().Contain("ExecuteDelete<WorkflowExecutionState>");
        guard.AcceptedBulkOperations.Should().Contain("ExecuteDelete<WorkflowSuspension>");
        guard.UntaggedRunDeletes.Should().BeEquivalentTo("ExecuteDelete<WorkflowExecutionState>", "ExecuteDelete<WorkflowSuspension>");
    }
}

internal sealed class WorkflowChangeLogGuard : IQueryExpressionInterceptor
{
    private const string Tag = "reeve-cluster: appended to change log";

    public List<string> AcceptedBulkOperations { get; } = [];
    public List<string> UntaggedRunDeletes { get; } = [];
    public List<string> Refused { get; } = [];

    public Expression QueryCompilationStarting(Expression query, QueryExpressionEventData eventData)
    {
        if (query is not MethodCallExpression bulk
            || bulk.Method.DeclaringType != typeof(EntityFrameworkQueryableExtensions)
            || bulk.Method.Name is not ("ExecuteUpdate" or "ExecuteDelete"))
            return query;

        var entityType = bulk.Method.GetGenericArguments()[0];
        var operation = bulk.Method.Name + "<" + entityType.Name + ">";
        var tagged = IsTagged(bulk.Arguments[0]);
        if (entityType == typeof(Workflow) || entityType == typeof(WorkflowVersion))
        {
            if (!tagged)
            {
                Refused.Add(operation);
                throw new InvalidOperationException("Change-log guard refused untagged " + operation);
            }
            AcceptedBulkOperations.Add(operation);
        }
        else if (bulk.Method.Name == "ExecuteDelete")
        {
            AcceptedBulkOperations.Add(operation);
            if (!tagged)
                UntaggedRunDeletes.Add(operation);
        }
        return query;
    }

    private static bool IsTagged(Expression source)
    {
        var call = source as MethodCallExpression;
        while (call is not null)
        {
            if (call.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
                && call.Method.Name == "TagWith" && call.Arguments.Count > 1
                && Equals((call.Arguments[1] as ConstantExpression)?.Value, Tag))
                return true;
            call = call.Arguments.Count == 0 ? null : call.Arguments[0] as MethodCallExpression;
        }
        return false;
    }
}
