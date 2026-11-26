using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Editor.Tests;

public sealed class NodeValidatorEditorTests
{
    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
    private const string WithNoop = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"b","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{"Note":"hi"}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"b","mappings":{}}]}""";
    private const string Unknown = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n1","blockTypeId":"gone.block","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"n1","mappings":{}}]}""";

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static IReadOnlyList<WorkflowValidationFinding> Refuse(WorkflowNodeValidationRequest request) =>
        [new WorkflowValidationFinding("host-says-no", "Not this node.") { NodeId = "b" }];

    [Fact]
    public async Task A_save_is_asked_in_the_save_phase_and_a_finding_is_reported_while_the_draft_saves()
    {
        var validator = new Validator();
        using var db = new SqliteEditorDatabase(configureHost: o => o.Seams.NodeValidator.UseInstance(validator));
        var created = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));
        validator.Answer = Refuse;

        var saved = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, WithNoop, AutoPublish: false)));

        var asked = validator.Asked.Last();
        asked.Phase.Should().Be(WorkflowValidationPhase.Save);
        var node = asked.Nodes.Single(n => n.NodeId == "b");
        (node.BlockTypeId, node.Configuration["Note"].GetString()).Should().Be(("noop", "hi"));
        var diagnostic = saved.Diagnostics.Should().ContainSingle().Subject;
        (diagnostic.RuleId, diagnostic.NodeId).Should().Be(("host-says-no", "b"));
        saved.VersionPublished.Should().BeFalse();
        (await db.ReadAsync(c => c.WorkflowVersions.CountAsync(v => v.WorkflowId == created.Workflow.Id))).Should().Be(2);
    }

    [Fact]
    public async Task A_publish_with_a_finding_is_not_promoted_and_names_the_finding()
    {
        var validator = new Validator();
        using var db = new SqliteEditorDatabase(configureHost: o => o.Seams.NodeValidator.UseInstance(validator));
        var created = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));
        await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, WithNoop, AutoPublish: false)));
        validator.Answer = Refuse;

        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        (await publish.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Diagnostics
            .Should().ContainSingle().Which.RuleId.Should().Be("host-says-no");
        validator.Asked.Last().Phase.Should().Be(WorkflowValidationPhase.Publish);
        var row = await db.ReadAsync(c => c.Workflows.AsNoTracking().SingleAsync(w => w.Id == created.Workflow.Id));
        row.CurrentVersionId.Should().Be(created.VersionId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_validator_that_throws_or_answers_nothing_blocks_the_publish_with_host_validator_faulted(bool throws)
    {
        var validator = new Validator();
        using var db = new SqliteEditorDatabase(configureHost: o => o.Seams.NodeValidator.UseInstance(validator));
        var created = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));
        validator.Answer = _ => throws ? throw new InvalidOperationException("secret detail") : null!;

        var saved = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, WithNoop, AutoPublish: true)));

        saved.VersionPublished.Should().BeFalse();
        var diagnostic = saved.Diagnostics.Should().ContainSingle().Subject;
        diagnostic.RuleId.Should().Be(WorkflowEditorFindingCodes.HostValidatorFaulted);
        diagnostic.Message.Should().NotContain("secret detail");
        var row = await db.ReadAsync(c => c.Workflows.AsNoTracking().SingleAsync(w => w.Id == created.Workflow.Id));
        (row.CurrentVersionId, row.DraftVersionId).Should().Be((created.VersionId, saved.VersionId));
    }

    [Theory]
    [InlineData("finding")]
    [InlineData("fault")]
    [InlineData("clean")]
    public async Task A_rollback_commits_its_copy_then_asks_the_validator_and_a_finding_or_fault_leaves_the_copy_not_current(string answer)
    {
        var validator = new Validator();
        using var db = new SqliteEditorDatabase(configureHost: o => o.Seams.NodeValidator.UseInstance(validator));
        var created = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, WithNoop)));
        var second = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, Valid, AutoPublish: true)));
        validator.Asked.Clear();
        validator.Answer = answer switch
        {
            "finding" => Refuse,
            "fault" => _ => throw new InvalidOperationException("secret detail"),
            _ => _ => [],
        };

        var rolled = await db.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));

        validator.Asked.Should().ContainSingle().Which.Phase.Should().Be(WorkflowValidationPhase.Publish);
        var row = await db.ReadAsync(c => c.Workflows.AsNoTracking().Include(w => w.CurrentVersion).Include(w => w.DraftVersion)
            .SingleAsync(w => w.Id == created.Workflow.Id));
        (await db.ReadAsync(c => c.WorkflowVersions.CountAsync(v => v.WorkflowId == created.Workflow.Id))).Should().Be(3);
        if (answer == "clean")
        {
            rolled.VersionPublished.Should().BeTrue();
            row.CurrentVersionId.Should().Be(rolled.VersionId);
            row.DraftVersionId.Should().BeNull();
            return;
        }

        rolled.VersionPublished.Should().BeFalse();
        rolled.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(
            answer == "finding" ? "host-says-no" : WorkflowEditorFindingCodes.HostValidatorFaulted);
        row.CurrentVersionId.Should().Be(second.VersionId, "the copy is stored and not current");
        row.DraftVersionId.Should().Be(rolled.VersionId);
    }

    [Fact]
    public async Task A_version_that_does_not_compile_is_not_put_to_the_validator()
    {
        var validator = new Validator();
        using var db = new SqliteEditorDatabase(configureHost: o => o.Seams.NodeValidator.UseInstance(validator));
        var created = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));
        validator.Asked.Clear();

        var saved = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, Unknown, AutoPublish: false)));

        saved.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.BlockTypeUnknown);
        validator.Asked.Should().BeEmpty();
    }

    private sealed class Validator : IWorkflowNodeValidator
    {
        public List<WorkflowNodeValidationRequest> Asked { get; } = [];

        public Func<WorkflowNodeValidationRequest, IReadOnlyList<WorkflowValidationFinding>> Answer { get; set; } = _ => [];

        public ValueTask<IReadOnlyList<WorkflowValidationFinding>> ValidateAsync(
            WorkflowNodeValidationRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return ValueTask.FromResult(Answer(request));
        }
    }
}
