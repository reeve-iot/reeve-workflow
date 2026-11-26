using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Editor.Tests;

public sealed class WorkflowEditorTests
{
    private const string Definition = "{\"nodes\":[],\"edges\":[]}";

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid Stranger = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task A_block_that_may_park_the_run_is_refused_inside_a_container_nothing_can_re_enter()
    {
        using var db = new SqliteEditorDatabase(
            configureHost: options => options.Seams.Blocks.Use<WaitInsideBlocks>());

        var refusal = await db.Awaiting(d => d.EditAsync(editor =>
                editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, WaitInside("holds")))))
            .Should().ThrowAsync<InvalidWorkflowDefinitionException>();

        refusal.Which.Message.Should().Contain("'wait'", "the refusal names the block that may park");
        refusal.Which.Message.Should().Contain("'box'",
            "and the container nothing can put the run back inside, by the id the author reads on the canvas");
    }

    [Fact]
    public async Task A_block_that_may_park_the_run_is_allowed_inside_a_container_that_only_drives_its_sub_canvases()
    {
        using var db = new SqliteEditorDatabase(
            configureHost: options => options.Seams.Blocks.Use<WaitInsideBlocks>());

        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, WaitInside("drives"))));

        created.VersionNumber.Should().Be(1);
    }

    private static string WaitInside(string containerTypeId) => System.Text.Json.JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new
            {
                id = "box", blockTypeId = containerTypeId, position = new { x = 0, y = 0 }, configuration = new { },
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new { nodes = new object[] { new { id = "wait", blockTypeId = "waits", position = new { x = 0, y = 0 }, configuration = new { } } }, edges = Array.Empty<object>() }
                }
            }
        }, edges = Array.Empty<object>()
    });

    private sealed class WaitInsideBlocks : IWorkflowBlocks
    {
        private static readonly Stub Waits = new("waits") { Parks = true };
        private static readonly Stub Drives = new("drives") { Canvases = ["Body"], Drives = true };
        private static readonly Stub Holds = new("holds") { Canvases = ["Body"] };

        public IWorkflowBlockFactory? Find(string blockTypeId) => blockTypeId switch
        {
            "waits" => Waits,
            "drives" => Drives,
            "holds" => Holds,
            _ => null
        };

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [Waits, Drives, Holds];

        private sealed class Stub(string id) : IWorkflowBlockFactory
        {
            public string BlockTypeId => id;
            public string DisplayName => id;
            public string Description => string.Empty;
            public string Category => "test";
            public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
            public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
            public IReadOnlyCollection<string> Canvases { get; init; } = [];
            public IReadOnlyCollection<string> SubCanvasNames => Canvases;
            public bool Parks { get; init; }
            public bool Drives { get; init; }
            public bool CanSuspend => Parks;
            public bool OnlyDrivesItsSubCanvases => Drives;
            public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task Create_publishes_the_first_version_and_makes_it_current()
    {
        using var db = new SqliteEditorDatabase();

        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        created.VersionNumber.Should().Be(1);
        created.VersionPublished.Should().BeTrue();

        var stored = await db.ReadAsync(context => context.Workflows
            .Include(w => w.CurrentVersion)
            .SingleAsync(w => w.Id == created.Workflow.Id));

        stored.CurrentVersion!.VersionNumber.Should().Be(1);
        stored.CurrentVersion.IsPublished.Should().BeTrue();
        stored.DraftVersionId.Should().BeNull();
    }

    [Fact]
    public async Task An_update_that_does_not_publish_leaves_the_running_version_alone()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        var updated = await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, "{\"nodes\":[{\"id\":\"a\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", AutoPublish: false)));

        updated.VersionPublished.Should().BeFalse();

        var stored = await db.ReadAsync(context => context.Workflows
            .Include(w => w.CurrentVersion).Include(w => w.DraftVersion)
            .SingleAsync(w => w.Id == created.Workflow.Id));

        stored.CurrentVersion!.VersionNumber.Should().Be(1,
            because: "a draft is what the editor saves while a graph is worked on, and the engine must "
                     + "keep running what was published until somebody says otherwise");
        stored.DraftVersion!.VersionNumber.Should().Be(2);
        stored.DraftVersion.IsPublished.Should().BeFalse();
    }

    [Fact]
    public async Task A_second_draft_save_amends_the_draft_rather_than_making_a_version_per_keystroke()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, "{\"nodes\":[{\"id\":\"a\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", AutoPublish: false)));
        await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, "{\"nodes\":[{\"id\":\"b\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", AutoPublish: false)));

        var versions = await db.ReadAsync(context => context.WorkflowVersions
            .Where(v => v.WorkflowId == created.Workflow.Id).ToListAsync());

        versions.Should().HaveCount(2);
        versions.Single(v => !v.IsPublished).DefinitionJson.Should().Contain("\"b\"");
    }

    [Fact]
    public async Task Publish_promotes_the_draft_and_leaves_none_behind()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, "{\"nodes\":[{\"id\":\"a\",\"blockTypeId\":\"WorkflowInput\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", AutoPublish: false)));

        var published = await db.EditAsync(editor => editor.PublishAsync(Owner, created.Workflow.Id));

        published.VersionNumber.Should().Be(2);
        var stored = await db.ReadAsync(context => context.Workflows
            .Include(w => w.CurrentVersion).SingleAsync(w => w.Id == created.Workflow.Id));
        stored.CurrentVersion!.VersionNumber.Should().Be(2);
        stored.DraftVersionId.Should().BeNull();
    }

    [Fact]
    public async Task Publishing_without_a_draft_is_refused_by_name()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        var publish = () => db.EditAsync(editor => editor.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<NoDraftVersionToPublishException>();
    }

    [Fact]
    public async Task Rollback_appends_a_new_version_rather_than_rewriting_history()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, "{\"nodes\":[{\"id\":\"a\",\"blockTypeId\":\"WorkflowInput\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}")));

        var rolled = await db.EditAsync(editor => editor.RollbackAsync(Owner, created.Workflow.Id, 1));

        rolled.VersionNumber.Should().Be(3);
        var versions = await db.ReadAsync(context => context.WorkflowVersions
            .Where(v => v.WorkflowId == created.Workflow.Id).OrderBy(v => v.VersionNumber).ToListAsync());
        versions.Select(v => v.VersionNumber).Should().Equal(1, 2, 3);
        versions[2].DefinitionJson.Should().Be(Definition);
        versions[2].ChangeMessage.Should().Be("Rollback to version 1");
    }

    [Fact]
    public async Task Rolling_back_to_the_version_already_running_is_refused()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        var rollback = () => db.EditAsync(editor => editor.RollbackAsync(Owner, created.Workflow.Id, 1));

        await rollback.Should().ThrowAsync<CannotRollbackToActiveVersionException>();
    }

    [Fact]
    public async Task Rolling_back_to_a_version_that_is_not_there_says_so()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        var rollback = () => db.EditAsync(editor => editor.RollbackAsync(Owner, created.Workflow.Id, 9));

        await rollback.Should().ThrowAsync<WorkflowVersionNotFoundException>();
    }

    [Theory]
    [InlineData("Whenever")]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("-1")]
    public async Task A_checkpoint_policy_that_says_nothing_this_package_reads_is_refused(string mode)
    {
        using var db = new SqliteEditorDatabase();

        var create = () => db.EditAsync(editor => editor.CreateAsync(Owner,
            new CreateWorkflowRequest("Ship", null, Definition, CheckpointPolicyJson: $$"""{"Mode":"{{mode}}"}""")));

        (await create.Should().ThrowAsync<InvalidWorkflowEditRequestException>())
            .WithMessage($"*{mode}*", "the refusal names what was written, not only that something was");
    }

    [Fact]
    public async Task A_checkpoint_policy_with_no_heartbeat_behind_it_is_refused()
    {
        using var db = new SqliteEditorDatabase(configureEngine: options =>
        {
            options.RunHeartbeatInterval = null;
            options.Recovery.Enabled = false;
        });

        var create = () => db.EditAsync(editor => editor.CreateAsync(Owner,
            new CreateWorkflowRequest("Ship", null, Definition,
                CheckpointPolicyJson: """{"Mode":"BeforeNonIdempotentOnly"}""")));

        (await create.Should().ThrowAsync<InvalidWorkflowEditRequestException>())
            .WithMessage("*RunHeartbeatInterval*");
    }

    [Fact]
    public async Task A_checkpoint_policy_whose_interval_outlives_the_sweep_s_cutoff_is_refused()
    {
        using var db = new SqliteEditorDatabase(configureEngine: options =>
        {
            options.Recovery.AbandonedAfter = TimeSpan.FromMinutes(1);
            options.RunHeartbeatInterval = TimeSpan.FromSeconds(10);
        });

        var create = () => db.EditAsync(editor => editor.CreateAsync(Owner,
            new CreateWorkflowRequest("Ship", null, Definition,
                CheckpointPolicyJson: """{"Mode":"Heartbeat","HeartbeatSeconds":60}""")));

        (await create.Should().ThrowAsync<InvalidWorkflowEditRequestException>())
            .WithMessage("*AbandonedAfter*", "a checkpoint older than the cutoff is re-entered from behind the run");
    }

    [Fact]
    public async Task A_checkpoint_policy_the_engine_allows_is_stored_on_the_workflow()
    {
        using var db = new SqliteEditorDatabase(configureEngine: options =>
        {
            options.Recovery.AbandonedAfter = TimeSpan.FromMinutes(1);
            options.RunHeartbeatInterval = TimeSpan.FromSeconds(10);
        });
        const string policy = """{"Mode":"Heartbeat","HeartbeatSeconds":30}""";

        var created = await db.EditAsync(editor => editor.CreateAsync(Owner,
            new CreateWorkflowRequest("Ship", null, Definition, CheckpointPolicyJson: policy)));

        var stored = await db.ReadAsync(context => context.Workflows.SingleAsync(w => w.Id == created.Workflow.Id));
        stored.CheckpointPolicyJson.Should().Be(policy, "a run reads the column, not the response");
        created.Workflow.CheckpointPolicyJson.Should().Be(policy);
    }

    [Fact]
    public async Task An_update_takes_a_checkpoint_policy_off_again()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner,
            new CreateWorkflowRequest("Ship", null, Definition, CheckpointPolicyJson: """{"Mode":"EveryBlock"}""")));

        await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, Definition)));

        var stored = await db.ReadAsync(context => context.Workflows.SingleAsync(w => w.Id == created.Workflow.Id));
        stored.CheckpointPolicyJson.Should().BeNull(
            "an update states the whole workflow, as it does for the retry policy, so leaving it out takes it off");
    }

    [Theory]
    [InlineData("[]", "an array where a canvas is required (rule L7, at '')")]
    [InlineData("{\"nodes\":[{\"id\":\"a\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"a\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", "second node with id 'a' on this canvas (rule duplicate-node-id")]
    [InlineData("{\"nodes\":[{\"id\":\"a\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"b\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"Nodes\":[{\"id\":\"x\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"x\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", "a key 'Nodes' where 'nodes' is meant; keys match exactly (rule L5, at '/Nodes')")]
    [InlineData("{\"nodes\":[{\"id\":\"a\",\"ID\":\"x\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"b\",\"ID\":\"x\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", "(rule L5, at '/nodes/0/ID')")]
    [InlineData("not json", "not one JSON value (line 1, byte 2) (rule L1, at '')")]
    [InlineData("""{"nodes":[{"id":"l","subCanvases":{"Body":{"nodes":[{"id":"x","position":{"x":0,"y":0},"configuration":{}},{"id":"x","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "second node with id 'x' on this canvas (rule duplicate-node-id")]
    [InlineData("""{"nodes":[{"id":"a","config":{},"position":{"x":0,"y":0}}],"edges":[]}""", "unlisted key 'config' on node (rule stored-key-unknown, at '/nodes/0/config')")]
    [InlineData("""{"nodes":[],"connections":[],"edges":[]}""", "unlisted key 'connections' on canvas (rule stored-key-unknown, at '/connections')")]
    [InlineData("""{"nodes":[{"id":"a","position":{"x":0,"y":0},"configuration":{}}],"nodes":[{"id":"b","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "(rule L3, at '/nodes')")]
    [InlineData("""{"nodes":[{"id":"a","position":{"x":1e400,"y":0},"configuration":{}}],"edges":[]}""", "(rule L7, at '/nodes/0/position/x')")]
    [InlineData("""{"nodes":[{"id":"\ud800","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "(rule L4, at '/nodes/0/id')")]
    public async Task A_definition_the_engine_could_not_load_is_refused_at_the_door(string json, string why)
    {
        using var db = new SqliteEditorDatabase();

        var create = () => db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, json)));

        (await create.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).WithMessage($"*{why}*");
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"twin","position":{"x":0,"y":0},"configuration":{}},{"id":"twin","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/1/id")]
    [InlineData("""{"nodes":[{"id":"loop","subCanvases":{"Body":{"nodes":[{"id":"twin","position":{"x":0,"y":0},"configuration":{}},{"id":"twin","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/subCanvases/Body/nodes/1/id")]
    public async Task An_update_with_a_duplicate_node_id_is_refused_with_its_rule_and_id(string definition, string pointer)
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        var update = () => db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, definition, AutoPublish: false)));

        var refusal = (await update.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).Which;
        refusal.Message.Should().Contain("duplicate-node-id").And.Contain("id 'twin'").And.Contain($"at '{pointer}'");
        var versions = await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.WorkflowId == created.Workflow.Id).ToListAsync());
        versions.Should().ContainSingle().Which.DefinitionJson.Should().Be(Definition);
    }

    [Theory]
    [InlineData("""[{"type":"Int"}]""", null, "The workflow's declared input parameters cannot be read")]
    [InlineData(null, """[{"name":"n","name":"m","type":"Int"}]""", "The workflow's declared output parameters cannot be read")]
    public async Task A_parameter_list_the_reader_refuses_is_refused_on_create_and_on_update(string? inputs, string? outputs, string why)
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        var create = () => db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition, inputs, outputs)));
        var update = () => db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, Definition, inputs, outputs)));

        (await create.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).WithMessage($"*{why}*");
        (await update.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).WithMessage($"*{why}*");
    }

    [Fact]
    public async Task An_update_under_an_unlisted_key_is_refused_and_the_stored_version_kept()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        var update = () => db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, """{"nodes":[],"Connections":[],"edges":[]}""")));

        (await update.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).WithMessage("*unlisted key 'Connections' on canvas (rule stored-key-unknown*");
        var versions = await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.WorkflowId == created.Workflow.Id).ToListAsync());
        versions.Should().ContainSingle().Which.DefinitionJson.Should().Be(Definition);
    }

    [Fact]
    public async Task A_malformed_port_type_name_is_stored_since_the_compiler_reads_it_and_reports_it()
    {
        using var db = new SqliteEditorDatabase();
        const string malformed = """{"nodes":[{"id":"a","blockTypeId":"x","outputPorts":[{"name":"p","type":"CollectionInt"}],"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";

        var created = await db.EditAsync(editor => editor.CreateAsync(Owner,
            new CreateWorkflowRequest("Ship", null, malformed, """[{"name":"n","type":"List<Int"}]""")));

        var version = await db.ReadAsync(context => context.WorkflowVersions.SingleAsync(v => v.WorkflowId == created.Workflow.Id));
        version.DefinitionJson.Should().Be(malformed);
        version.InputParametersJson.Should().Be("""[{"name":"n","type":"List<Int"}]""");
    }

    [Fact]
    public async Task A_write_by_somebody_who_does_not_own_it_is_answered_as_if_it_were_not_there()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        var update = () => db.EditAsync(editor => editor.UpdateAsync(Stranger, created.Workflow.Id,
            new UpdateWorkflowRequest("Stolen", null, Definition)));

        await update.Should().ThrowAsync<WorkflowNotFoundException>();
        var stored = await db.ReadAsync(context => context.Workflows.SingleAsync(w => w.Id == created.Workflow.Id));
        stored.Name.Should().Be("Ship");
    }

    [Fact]
    public async Task Delete_takes_the_run_rows_that_hold_the_workflow_back_with_it()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        await SeedRunAsync(db, created.Workflow.Id, WorkflowRunStateStatus.Completed);

        await db.EditAsync(editor => editor.DeleteAsync(Owner, created.Workflow.Id));

        (await db.ReadAsync(context => context.Workflows.AnyAsync(w => w.Id == created.Workflow.Id)))
            .Should().BeFalse();
        (await db.ReadAsync(context => context.WorkflowExecutionStates.AnyAsync(e => e.WorkflowId == created.Workflow.Id)))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(WorkflowRunStateStatus.Running)]
    [InlineData(WorkflowRunStateStatus.Sleeping)]
    [InlineData(WorkflowRunStateStatus.Waiting)]
    [InlineData(WorkflowRunStateStatus.Pending)]
    [InlineData(WorkflowRunStateStatus.Suspended)]
    public async Task Delete_is_refused_while_a_run_can_still_come_back(WorkflowRunStateStatus status)
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        await SeedRunAsync(db, created.Workflow.Id, status);

        var delete = () => db.EditAsync(editor => editor.DeleteAsync(Owner, created.Workflow.Id));

        await delete.Should().ThrowAsync<WorkflowHasActiveExecutionsException>();
        (await db.ReadAsync(context => context.Workflows.AnyAsync(w => w.Id == created.Workflow.Id)))
            .Should().BeTrue();
    }

    private static Task<int> SeedRunAsync(SqliteEditorDatabase db, Guid workflowId, WorkflowRunStateStatus status) =>
        db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<ReeveWorkflow.EntityFrameworkCore.WorkflowDbContext>();
            context.WorkflowExecutionStates.Add(new WorkflowExecutionState(
                Guid.NewGuid(), workflowId, workflowVersion: 1, status.ToString(),
                variablesJson: "{}", triggerSource: null, ownedByReplicaId: null,
                startedAtUtc: SqliteEditorDatabase.Clock.GetUtcNow()));
            return await context.SaveChangesAsync();
        });

    private const string UnreadableRow = """{"nodes":[{"id":"a","blockTypeId":"Harmless","position":{"x":0,"y":0},"configuration":{}}],"Nodes":[{"id":"a","blockTypeId":"RunScript","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task An_update_the_reader_refuses_is_refused_on_either_branch_and_writes_nothing(bool autoPublish, bool draftFirst)
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        if (draftFirst)
        {
            await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
                new UpdateWorkflowRequest("Ship", null, """{"nodes":[{"id":"d","blockTypeId":"x","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", AutoPublish: false)));
        }

        var update = () => db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, UnreadableRow, AutoPublish: autoPublish)));

        (await update.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).WithMessage("*rule L5*");
        var texts = await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.WorkflowId == created.Workflow.Id)
            .Select(v => v.DefinitionJson).ToListAsync());
        texts.Should().NotContain(UnreadableRow);
    }

    [Theory]
    [InlineData(UnreadableRow, null, null, "*rule L5*")]
    [InlineData("{\"nodes\":[{\"id\":\"twin\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"twin\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", null, null, "*id 'twin'*duplicate-node-id*")]
    [InlineData("""{"nodes":[{"id":"loop","subCanvases":{"Body":{"nodes":[{"id":"twin","position":{"x":0,"y":0},"configuration":{}},{"id":"twin","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", null, null, "*id 'twin'*duplicate-node-id*at '/nodes/0/subCanvases/Body/nodes/1/id'*")]
    [InlineData(Definition, """[{"name":"n","type":"Int"}] x""", null, "*input parameters cannot be read*")]
    [InlineData(Definition, null, """[{"name":"n","type":"Int"}] x""", "*output parameters cannot be read*")]
    public async Task A_draft_the_reader_refuses_is_not_published(string definition, string? inputs, string? outputs, string why)
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        var draft = await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, """{"nodes":[{"id":"d","blockTypeId":"x","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", AutoPublish: false)));
        await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.Id == draft.VersionId)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, definition)
                .SetProperty(v => v.InputParametersJson, inputs).SetProperty(v => v.OutputParametersJson, outputs)));

        var publish = () => db.EditAsync(editor => editor.PublishAsync(Owner, created.Workflow.Id));

        (await publish.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).WithMessage(why);
        var workflow = await db.ReadAsync(context => context.Workflows.AsNoTracking().SingleAsync(w => w.Id == created.Workflow.Id));
        workflow.CurrentVersionId.Should().Be(created.VersionId);
        workflow.DraftVersionId.Should().Be(draft.VersionId);
    }

    [Theory]
    [InlineData(UnreadableRow, null, null, "*rule L5*")]
    [InlineData("{\"nodes\":[{\"id\":\"twin\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"twin\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", null, null, "*id 'twin'*duplicate-node-id*")]
    [InlineData("""{"nodes":[{"id":"loop","subCanvases":{"Body":{"nodes":[{"id":"twin","position":{"x":0,"y":0},"configuration":{}},{"id":"twin","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", null, null, "*id 'twin'*duplicate-node-id*at '/nodes/0/subCanvases/Body/nodes/1/id'*")]
    [InlineData(Definition, """[{"name":"n","type":"Int"}] x""", null, "*input parameters cannot be read*")]
    [InlineData(Definition, null, """[{"name":"n","type":"Int"}] x""", "*output parameters cannot be read*")]
    public async Task A_rollback_to_a_version_the_reader_refuses_is_refused_and_copies_nothing(string definition, string? inputs, string? outputs, string why)
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.Id == created.VersionId)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, definition)
                .SetProperty(v => v.InputParametersJson, inputs).SetProperty(v => v.OutputParametersJson, outputs)));
        await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, """{"nodes":[{"id":"b","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""")));

        var rollback = () => db.EditAsync(editor => editor.RollbackAsync(Owner, created.Workflow.Id, 1));

        (await rollback.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).WithMessage(why);
        var numbers = await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.WorkflowId == created.Workflow.Id)
            .Select(v => v.VersionNumber).ToListAsync());
        numbers.Should().BeEquivalentTo([1, 2]);
    }

    private const string ChildChosenByNoId = """{"nodes":[{"id":"s","blockTypeId":"SubWorkflow","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
    private const string UnreadableList = """[{"name":"n","type":"Int"}] x""";

    [Fact]
    public async Task Publish_reads_the_draft_before_it_checks_its_sub_workflows()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        var draft = await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, """{"nodes":[{"id":"d","blockTypeId":"x","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", AutoPublish: false)));
        await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.Id == draft.VersionId)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, ChildChosenByNoId)
                .SetProperty(v => v.InputParametersJson, UnreadableList)));

        var publish = () => db.EditAsync(editor => editor.PublishAsync(Owner, created.Workflow.Id));

        (await publish.Should().ThrowAsync<InvalidWorkflowDefinitionException>())
            .WithMessage("The workflow is invalid: The workflow's declared input parameters cannot be read*");
    }

    [Fact]
    public async Task Rollback_reads_the_target_before_it_checks_its_sub_workflows()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.Id == created.VersionId)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, ChildChosenByNoId)
                .SetProperty(v => v.OutputParametersJson, UnreadableList)));
        await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, """{"nodes":[{"id":"b","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""")));

        var rollback = () => db.EditAsync(editor => editor.RollbackAsync(Owner, created.Workflow.Id, 1));

        (await rollback.Should().ThrowAsync<InvalidWorkflowDefinitionException>())
            .WithMessage("The workflow is invalid: The workflow's declared output parameters cannot be read*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_settings_only_update_over_a_current_version_the_reader_refuses_goes_through(bool autoPublish)
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.Id == created.VersionId)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, UnreadableRow)));

        var result = await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Renamed", null, UnreadableRow, AutoPublish: autoPublish)));

        result.VersionId.Should().BeNull("no version text was written");
        var workflow = await db.ReadAsync(context => context.Workflows.AsNoTracking().SingleAsync(w => w.Id == created.Workflow.Id));
        workflow.Name.Should().Be("Renamed");
        workflow.CurrentVersionId.Should().Be(created.VersionId);
        (await db.ReadAsync(context => context.WorkflowVersions.CountAsync(v => v.WorkflowId == created.Workflow.Id))).Should().Be(1);
    }

    [Fact]
    public async Task A_default_save_equal_to_a_draft_the_reader_refuses_is_refused_whole_and_a_draft_save_keeps_its_settings()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        var draft = await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, """{"nodes":[{"id":"d","blockTypeId":"x","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", AutoPublish: false)));
        await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.Id == draft.VersionId)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, UnreadableRow)));

        var refused = async () => await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Renamed", null, UnreadableRow)));

        (await refused.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).Which.Message.Should().Contain("rule L5");
        var untouched = await db.ReadAsync(context => context.Workflows.AsNoTracking().SingleAsync(w => w.Id == created.Workflow.Id));
        untouched.Name.Should().Be("Ship", "nothing commits");

        var stored = await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Renamed", null, UnreadableRow, AutoPublish: false)));

        (stored.VersionPublished, stored.Fault).Should().Be((false, null));
        var workflow = await db.ReadAsync(context => context.Workflows.AsNoTracking().SingleAsync(w => w.Id == created.Workflow.Id));
        (workflow.Name, workflow.CurrentVersionId, workflow.DraftVersionId).Should().Be(("Renamed", created.VersionId, draft.VersionId));
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"s","blockTypeId":"SubWorkflow","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"s","mappings":{}}]}""", null, "workflow selected")]
    [InlineData(Definition, """[{"name":"n","type":"Int"},""", "input")]
    [InlineData(Definition, null, "output")]
    public async Task A_default_save_equal_to_a_draft_publish_would_refuse_is_refused_whole(string definition, string? inputs, string named)
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        var draft = await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, """{"nodes":[{"id":"d","blockTypeId":"x","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", AutoPublish: false)));
        var outputs = named == "output" ? """{"not":"a list"}""" : null;
        await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.Id == draft.VersionId)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, definition)
                .SetProperty(v => v.InputParametersJson, inputs).SetProperty(v => v.OutputParametersJson, outputs)));

        var refused = async () => await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Renamed", null, definition, inputs, outputs)));

        (await refused.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).Which.Message.Should().Contain(named);
        (await db.ReadAsync(context => context.Workflows.AsNoTracking().SingleAsync(w => w.Id == created.Workflow.Id))).Name
            .Should().Be("Ship", "nothing commits");
    }

    [Fact]
    public async Task A_default_save_of_readable_text_into_a_draft_the_reader_refuses_publishes_it()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        var draft = await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, """{"nodes":[{"id":"d","blockTypeId":"x","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", AutoPublish: false)));
        await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.Id == draft.VersionId)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, UnreadableRow)));

        var fixedUp = await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""")));

        (fixedUp.VersionPublished, fixedUp.VersionId, fixedUp.Fault).Should().Be((true, draft.VersionId, null));
    }

    [Fact]
    public async Task A_save_that_repeats_an_unreadable_current_version_over_a_draft_discards_the_draft()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, """{"nodes":[{"id":"d","blockTypeId":"x","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", AutoPublish: false)));
        await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.Id == created.VersionId)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, UnreadableRow)));

        var result = await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, UnreadableRow, AutoPublish: false)));

        result.VersionId.Should().BeNull();
        var workflow = await db.ReadAsync(context => context.Workflows.AsNoTracking().SingleAsync(w => w.Id == created.Workflow.Id));
        (workflow.CurrentVersionId, workflow.DraftVersionId).Should().Be((created.VersionId, (Guid?)null));
    }

    [Fact]
    public async Task An_update_that_changes_one_text_of_an_unreadable_version_is_refused_for_the_text_it_writes()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(editor => editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));
        await db.ReadAsync(context => context.WorkflowVersions.Where(v => v.Id == created.VersionId)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, UnreadableRow)));

        var update = () => db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Renamed", null, UnreadableRow, "[]", AutoPublish: false)));

        (await update.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).WithMessage("*rule L5*");
        (await db.ReadAsync(context => context.Workflows.AsNoTracking().SingleAsync(w => w.Id == created.Workflow.Id)))
            .Name.Should().Be("Ship");
    }

    [Fact]
    public void Every_public_check_runs_the_reader_first()
    {
        (string Definition, string Why)[] cases =
        [
            ("{\"nodes\":[],\"x\":\"" + new string('\ud800', 1) + "\",\"edges\":[]}", "*rule L4*"),
            ("\u00a0", "*rule L1*"),
            (UnreadableRow, "*rule L5*"),
            ("""{"nodes":[{"id":"a","blockTypeId":"x","position":{"x":1e400,"y":0},"configuration":{}}],"edges":[]}""", "*rule L7*"),
            ("null", "*rule L7*"),
            (" ", "*rule L1*"),
        ];

        foreach (var (definition, why) in cases)
        {
            Action[] checks =
            [
                () => WorkflowDefinitionAdmission.EnsureReadable(definition, null, null),
                () => WorkflowDefinitionAdmission.Ensure(definition),
                () => WorkflowDefinitionAdmission.EnsureSubWorkflowReferencesStatic(definition),
                () => WorkflowDefinitionAdmission.EnsureWaitsCanBeParked(definition, new WaitInsideBlocks()),
            ];
            foreach (var check in checks)
                check.Should().Throw<InvalidWorkflowDefinitionException>().WithMessage(why);
        }
    }

    [Fact]
    public void EnsureReadable_reads_both_parameter_lists_and_passes_what_the_reader_reads()
    {
        var input = () => WorkflowDefinitionAdmission.EnsureReadable(Definition, UnreadableList, null);
        var output = () => WorkflowDefinitionAdmission.EnsureReadable(Definition, null, UnreadableList);
        var blank = () => WorkflowDefinitionAdmission.EnsureReadable(Definition, " ", null);
        var jsonNull = () => WorkflowDefinitionAdmission.EnsureReadable(Definition, null, "null");
        var readable = () => WorkflowDefinitionAdmission.EnsureReadable(Definition, "[]", null);
        var none = () => WorkflowDefinitionAdmission.EnsureReadable(Definition, null, null);

        input.Should().Throw<InvalidWorkflowDefinitionException>().WithMessage("*input parameters cannot be read*");
        output.Should().Throw<InvalidWorkflowDefinitionException>().WithMessage("*output parameters cannot be read*");
        blank.Should().Throw<InvalidWorkflowDefinitionException>().WithMessage("*input parameters cannot be read: a parameter list is blank*rule L7*");
        jsonNull.Should().Throw<InvalidWorkflowDefinitionException>().WithMessage("*output parameters cannot be read: a parameter list is the JSON null*rule L7*");
        readable.Should().NotThrow();
        none.Should().NotThrow();
    }

    [Fact]
    public void A_raw_unpaired_surrogate_is_the_readers_refusal()
    {
        var lone = new string('\ud800', 1);
        (string Definition, string? Inputs)[] cases =
        [
            ("{\"nodes\":[],\"x\":\"" + lone + "\",\"edges\":[]}", null),
            ("{\"nodes\":[{\"id\":\"a\",\"blockTypeId\":\"x\",\"configuration\":{\"s\":\"a" + lone + "\"},\"position\":{\"x\":0,\"y\":0}}],\"edges\":[]}", null),
            (Definition, "[{\"name\":\"a" + lone + "\",\"type\":\"Int\"}]"),
        ];

        foreach (var (definition, inputs) in cases)
        {
            var ensure = () => WorkflowDefinitionAdmission.Ensure(definition, inputs, null);
            ensure.Should().Throw<InvalidWorkflowDefinitionException>().WithMessage("*rule L4*");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u00a0")]
    [InlineData("[]")]
    [InlineData("null")]
    public void An_absent_blank_or_non_object_definition_is_the_readers_refusal(string? definition)
    {
        var ensure = () => WorkflowDefinitionAdmission.Ensure(definition);

        ensure.Should().Throw<InvalidWorkflowDefinitionException>().WithMessage("*rule L*");
    }
}
