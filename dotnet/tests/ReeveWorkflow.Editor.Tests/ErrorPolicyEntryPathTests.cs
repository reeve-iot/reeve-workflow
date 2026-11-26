using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class ErrorPolicyEntryPathTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
    private const string ValidB = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"b","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"b","mappings":{}}]}""";

    private static readonly string?[] ConfigPolicies =
    [
        null, "null", """{"policy":"FailWorkflow"}""", """{"policy":"ContinueWithDefault","defaultOutputJson":"{}"}""",
        """{"policy":"EmitError","routeToErrorPort":true}""", """{"policy":"skip"}""", """{"policy":"Skip","routeToErrorPort":true}""",
    ];

    private static readonly string?[] NodePolicies =
    [
        null, "null", """{"policy":"FailWorkflow","routeToErrorPort":true}""", """{"policy":"ContinueWithDefault","defaultOutputJson":"{}"}""",
        """{"policy":"EmitError","routeToErrorPort":false}""", """{"policy":"Skip"}""",
    ];

    public static TheoryData<int, int> Placements()
    {
        var data = new TheoryData<int, int>();
        for (var c = 0; c < ConfigPolicies.Length; c++)
            for (var n = 0; n < NodePolicies.Length; n++)
                data.Add(c, n);
        return data;
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public async Task Every_road_moves_the_policy_or_refuses_it_there(int config, int node)
    {
        var document = Document(ConfigPolicies[config], NodePolicies[node]);
        var readerRefuses = ConfigPolicies[config] == "null" || NodePolicies[node] == "null"
            || NodePolicies[node]?.Contains("routeToErrorPort", StringComparison.Ordinal) == true;
        var readerRule = ConfigPolicies[config] == "null" || NodePolicies[node] == "null" ? CompilationRules.DefinitionUnreadable
            : CompilationRules.ErrorPolicyRouteUnread;
        var unread = new List<string>();
        if (ConfigPolicies[config] is not null)
            unread.Add(CompilationRules.ErrorPolicyInConfiguration);
        using var db = new SqliteEditorDatabase();

        var validate = () => db.InScopeAsync(sp => sp.GetRequiredService<IWorkflowCatalog>().ValidateWorkflowImportAsync(document, null, null));
        var saved = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Saved", null, Valid)));
        if (readerRefuses)
        {
            (await validate.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Diagnostics
                .Should().ContainSingle().Which.RuleId.Should().Be(readerRule);
            await db.Awaiting(d => d.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Created", null, document))))
                .Should().ThrowAsync<InvalidWorkflowDefinitionException>();
            await db.Awaiting(d => d.EditAsync(e => e.UpdateAsync(Owner, saved.Workflow.Id,
                    new UpdateWorkflowRequest("Saved", null, document, AutoPublish: true))))
                .Should().ThrowAsync<InvalidWorkflowDefinitionException>();
        }
        else
        {
            if (unread.Count > 0)
            {
                var refusal = (await validate.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Diagnostics;
                foreach (var diagnostic in PortTypeNameCheck.Check(document, null, null).Unread)
                    refusal.Should().Contain(d => d.RuleId == diagnostic.RuleId && d.Message == diagnostic.Message);
                refusal.Should().Contain(d => d.Message.Contains("move it onto the node", StringComparison.Ordinal));
            }
            else
            {
                await validate.Should().NotThrowAsync();
            }

            var created = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Created", null, document)));
            Refused(created.Diagnostics).Should().Equal(unread);
            created.VersionPublished.Should().Be(unread.Count == 0);

            var autoPublished = await db.EditAsync(e => e.UpdateAsync(Owner, saved.Workflow.Id,
                new UpdateWorkflowRequest("Saved", null, document, AutoPublish: true)));
            Refused(autoPublished.Diagnostics).Should().Equal(unread);
            autoPublished.VersionPublished.Should().Be(unread.Count == 0);

            var drafted = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Drafted", null, Valid)));
            await db.EditAsync(e => e.UpdateAsync(Owner, drafted.Workflow.Id, new UpdateWorkflowRequest("Drafted", null, document, AutoPublish: false)));
            var publish = () => db.EditAsync(e => e.PublishAsync(Owner, drafted.Workflow.Id));
            await Refuses(publish, unread);

            var rolled = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Rolled", null, Valid)));
            await OverwriteAsync(db, rolled.VersionId!.Value, document);
            await db.EditAsync(e => e.UpdateAsync(Owner, rolled.Workflow.Id, new UpdateWorkflowRequest("Rolled", null, ValidB, AutoPublish: true)));
            var rollback = () => db.EditAsync(e => e.RollbackAsync(Owner, rolled.Workflow.Id, 1));
            await Refuses(rollback, unread);
        }
    }

    private static List<string> Refused(IReadOnlyList<CompilationDiagnostic>? diagnostics) =>
        [.. (diagnostics ?? []).Select(d => d.RuleId).Where(r => r.StartsWith("error-policy-", StringComparison.Ordinal))];

    private static async Task Refuses(Func<Task<WorkflowEditResult>> door, List<string> unread)
    {
        if (unread.Count == 0)
        {
            (await door()).VersionPublished.Should().BeTrue();
            return;
        }

        Refused((await door.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Diagnostics).Should().Equal(unread);
    }

    private static Task<int> OverwriteAsync(SqliteEditorDatabase db, Guid versionId, string definition) =>
        db.ReadAsync(c => c.WorkflowVersions.Where(v => v.Id == versionId)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, definition)));

    private static string Document(string? configPolicy, string? nodePolicy)
    {
        var configuration = ",\"configuration\":{" + (configPolicy is null ? "" : "\"errorPolicy\":" + configPolicy) + "}";
        var policy = nodePolicy is null ? "" : ",\"errorPolicy\":" + nodePolicy;
        return "{\"nodes\":[{\"id\":\"start\",\"blockTypeId\":\"WorkflowInput\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"n\",\"blockTypeId\":\"noop\""
            + configuration + policy + ",\"position\":{\"x\":0,\"y\":0}}],\"edges\":[{\"id\":\"e\",\"sourceNodeId\":\"start\",\"targetNodeId\":\"n\",\"mappings\":{}}]}";
    }
}
