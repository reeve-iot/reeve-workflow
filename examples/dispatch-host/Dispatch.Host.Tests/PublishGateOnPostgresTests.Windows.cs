using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Editor;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

public sealed partial class PublishGateOnPostgresTests
{
    [Fact]
    public async Task A_default_save_equal_to_the_draft_conflicts_when_a_peer_rewrites_the_draft_before_its_own_write()
    {
        await using var desk = await DeskAsync("gate_s1_peer_rewrite");
        var created = await desk.CreateAsync(Valid);
        var draft = await desk.SaveAsync(created.Workflow.Id, ValidB);
        desk.Hooks.BeforeCommand(UpdatesWorkflows, () => desk.SaveAsync(created.Workflow.Id, ValidC));

        var gate = () => desk.SaveAsync(created.Workflow.Id, ValidB, autoPublish: true);

        await gate.Should().ThrowAsync<WorkflowDraftChangedException>();
        desk.Hooks.Fired.Should().Be(1);
        var row = await desk.RowAsync(created.Workflow.Id);
        (row.CurrentVersionId, row.DraftVersionId).Should().Be((created.VersionId, draft.VersionId));
        row.DraftVersion!.DefinitionJson.Should().Be(await desk.AsStoredAsync(ValidC));
    }

    public static IEnumerable<object[]> DoorsAndPeers() =>
        from door in Windows.Keys
        from peer in new[] { "save", "auto", "gate", "publish", "rollback", "discard", "delete" }
        select new object[] { door, peer };

    private static readonly Dictionary<string, string[]> Windows = new()
    {
        ["create"] = ["after its commit", "before its promote"],
        ["auto"] = ["before its bump", "after its commit", "before its promote"],
        ["newdraft"] = ["before its bump", "after its commit", "before its promote"],
        ["gate"] = ["before its settings write", "after its commit", "before its promote"],
        ["publish"] = ["before its promote"],
        ["rollback"] = ["before its bump"],
    };

    [Theory]
    [MemberData(nameof(DoorsAndPeers))]
    public async Task Every_doors_read_to_swap_window_holds_with_each_peer_in_it(string door, string peer)
    {
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("GATE_WINDOW_ITERATIONS"), out var n) ? n : 30;
        await using var desk = await DeskAsync($"gate_w_{door}_{peer}");
        var random = new Random((door + peer).Aggregate(17, (hash, c) => hash * 31 + c));
        var points = Windows[door];
        var violations = new List<string>();

        for (var i = 0; i < iterations; i++)
        {
            var name = $"w{i}";
            var id = door == "create" ? (Guid?)null : await SetUpAsync(desk, name, withDraft: door != "newdraft");
            var sent = door == "gate" ? await DraftOrCurrentAsync(desk, id!.Value) : Text(random, random.Next(3) != 0);
            Outcome? other = null;
            async Task Peer()
            {
                var target = id ?? await IdAsync(desk, name);
                other = target is { } found ? await RunAsync(desk, peer, found, random) : null;
            }

            var point = i % (points.Length + 1);
            Outcome own;
            if (point < points.Length)
            {
                Arm(desk, door, points[point], Peer);
                own = await RunDoorAsync(desk, door, name, id, sent);
            }
            else
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var (spinA, spinB) = (random.Next(4000), random.Next(4000));
                var doing = Task.Run(async () => { await start.Task; Thread.SpinWait(spinA); return await RunDoorAsync(desk, door, name, id, sent); });
                var peering = Task.Run(async () => { await start.Task; Thread.SpinWait(spinB); await Peer(); });
                start.SetResult();
                own = await doing.WaitAsync(TimeSpan.FromSeconds(60));
                await peering.WaitAsync(TimeSpan.FromSeconds(60));
            }
            desk.Hooks.Clear();

            var outcomes = other is null ? new[] { own } : [own, other];
            var at = point < points.Length ? points[point] : "at once";
            foreach (var violation in await ViolationsAsync(desk, id ?? await IdAsync(desk, name), outcomes))
                violations.Add($"#{i} {door} with {peer} {at}: {violation}");
        }

        violations.Should().BeEmpty();
    }

    private sealed record Outcome(string Op, string? Sent, bool Auto, WorkflowEditResult? Result, Exception? Error);

    private static string Text(Random random, bool valid)
    {
        var tag = "t" + random.Next(1_000_000);
        var block = valid ? "noop" : "gone.block";
        return $$$"""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"{{{tag}}}","blockTypeId":"{{{block}}}","position":{"x":240,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"{{{tag}}}","mappings":{}}]}""";
    }

    private static bool Compiles(string text) => !text.Contains("gone.block", StringComparison.Ordinal);

    private static async Task<Guid> SetUpAsync(Desk desk, string name, bool withDraft)
    {
        var created = await desk.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest(name, null, Valid)));
        var id = created.Workflow.Id;
        await desk.EditAsync(e => e.UpdateAsync(Owner, id, new UpdateWorkflowRequest(name, null, ValidB, AutoPublish: true)));
        if (withDraft)
            await desk.EditAsync(e => e.UpdateAsync(Owner, id, new UpdateWorkflowRequest(name, null, ValidC, AutoPublish: false)));
        return id;
    }

    private static void Arm(Desk desk, string door, string point, Func<Task> peer)
    {
        switch (point)
        {
            case "after its commit":
                desk.Hooks.AfterCommit(1, peer);
                break;
            case "before its promote" when door is "publish":
            case "before its bump":
            case "before its settings write":
                desk.Hooks.BeforeCommand(UpdatesWorkflows, peer);
                break;
            default:
                desk.Hooks.BeforeBegin(2, peer);
                break;
        }
    }

    private static async Task<Outcome> RunDoorAsync(Desk desk, string door, string name, Guid? id, string sent)
    {
        try
        {
            var result = door switch
            {
                "create" => await desk.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest(name, null, sent))),
                "publish" => await desk.EditAsync(e => e.PublishAsync(Owner, id!.Value)),
                "rollback" => await desk.EditAsync(e => e.RollbackAsync(Owner, id!.Value, 1)),
                _ => await desk.EditAsync(e => e.UpdateAsync(Owner, id!.Value, new UpdateWorkflowRequest(name, null, sent, AutoPublish: true))),
            };
            return new Outcome(door, door is "publish" or "rollback" ? null : sent, true, result, null);
        }
        catch (Exception error)
        {
            return new Outcome(door, sent, true, null, error);
        }
    }

    private static async Task<Outcome> RunAsync(Desk desk, string op, Guid id, Random random)
    {
        string? sent = null;
        try
        {
            var name = await desk.ReadAsync(c => c.Workflows.AsNoTracking().Where(w => w.Id == id).Select(w => w.Name).SingleOrDefaultAsync())
                ?? "gone";
            switch (op)
            {
                case "save":
                case "auto":
                    lock (random)
                        sent = Text(random, random.Next(3) != 0);
                    return new Outcome(op, sent, op == "auto", await desk.EditAsync(e => e.UpdateAsync(Owner, id,
                        new UpdateWorkflowRequest(name, null, sent, AutoPublish: op == "auto"))), null);
                case "gate":
                    sent = await DraftOrCurrentAsync(desk, id);
                    return new Outcome(op, sent, true, await desk.EditAsync(e => e.UpdateAsync(Owner, id,
                        new UpdateWorkflowRequest(name, null, sent))), null);
                case "discard":
                    var current = await desk.ReadAsync(c => c.Workflows.AsNoTracking().Where(w => w.Id == id)
                        .Select(w => w.CurrentVersion != null ? w.CurrentVersion.DefinitionJson : w.DraftVersion!.DefinitionJson)
                        .SingleOrDefaultAsync()) ?? Valid;
                    return new Outcome(op, null, false, await desk.EditAsync(e => e.UpdateAsync(Owner, id,
                        new UpdateWorkflowRequest(name, null, current, AutoPublish: false))), null);
                case "publish":
                    return new Outcome(op, null, false, await desk.EditAsync(e => e.PublishAsync(Owner, id)), null);
                case "rollback":
                    return new Outcome(op, null, false, await desk.EditAsync(e => e.RollbackAsync(Owner, id, 1)), null);
                default:
                    return new Outcome(op, null, false, await desk.EditAsync(e => e.DeleteAsync(Owner, id)), null);
            }
        }
        catch (Exception error)
        {
            return new Outcome(op, sent, op is "auto" or "gate", null, error);
        }
    }

    private static Task<Guid?> IdAsync(Desk desk, string name) =>
        desk.ReadAsync(c => c.Workflows.AsNoTracking().Where(w => w.Name == name).Select(w => (Guid?)w.Id).SingleOrDefaultAsync());

    private static Task<string> DraftOrCurrentAsync(Desk desk, Guid id) =>
        desk.ReadAsync(async c => await c.Workflows.AsNoTracking().Where(w => w.Id == id)
            .Select(w => w.DraftVersion != null ? w.DraftVersion.DefinitionJson : w.CurrentVersion!.DefinitionJson)
            .SingleOrDefaultAsync() ?? Valid);

    private static readonly HashSet<Type> Answers =
    [
        typeof(WorkflowDraftChangedException), typeof(WorkflowNotFoundException), typeof(NoDraftVersionToPublishException),
        typeof(WorkflowDoesNotCompileException), typeof(CannotRollbackToActiveVersionException),
    ];

    private static async Task<List<string>> ViolationsAsync(Desk desk, Guid? id, Outcome[] outcomes)
    {
        var found = new List<string>();
        foreach (var o in outcomes)
        {
            if (o.Error is { } error && !Answers.Contains(error.GetType()))
                found.Add($"{o.Op} threw {error.GetType().Name}: {error.Message}");
            if (o.Error is WorkflowDoesNotCompileException && o.Op is not ("publish" or "rollback"))
                found.Add($"{o.Op} threw WorkflowDoesNotCompileException");
            if (o.Result?.Fault is { } fault && fault is not WorkflowNotFoundException)
                found.Add($"{o.Op} faulted {fault.GetType().Name}: {fault.Message}");
        }
        if (outcomes.Any(o => o.Op == "delete" && o.Error is null))
        {
            if (id is { } gone && await desk.ReadAsync(c => c.Workflows.AnyAsync(w => w.Id == gone)))
                found.Add("a delete answered, and the workflow is there");
            return found;
        }
        if (id is not { } workflowId)
            return outcomes.Any(o => o.Op == "create" && o.Error is null) ? ["a create answered, and no workflow is there"] : found;

        var row = await desk.RowAsync(workflowId);
        var versions = await desk.ReadAsync(c => c.WorkflowVersions.AsNoTracking().Where(v => v.WorkflowId == workflowId).ToListAsync());
        var compiler = desk.Services.GetRequiredService<WorkflowCompilationService>();
        if (row.CurrentVersion is { IsPublished: false })
            found.Add("the current version is not published");
        if (row.DraftVersion is { IsPublished: true })
            found.Add("the draft is published");
        foreach (var published in versions.Where(v => v.IsPublished))
            if (!(await compiler.EnsureAsync(published, null, default)).IsValid)
                found.Add($"published version {published.VersionNumber} does not compile");

        var saves = outcomes.Where(o => o.Sent is not null && o.Result is { VersionId: not null, Fault: null }).ToList();
        foreach (var o in saves)
        {
            var result = o.Result!;
            var version = versions.SingleOrDefault(v => v.Id == result.VersionId);
            if (version is null)
            {
                found.Add($"{o.Op} reported version {result.VersionId}, which is not stored");
                continue;
            }
            var into = new List<string>();
            foreach (var p in saves.Where(p => p.Result!.VersionId == version.Id))
                into.Add(await desk.AsStoredAsync(p.Sent!));
            if (!into.Contains(version.DefinitionJson))
                found.Add($"{o.Op}: version {version.VersionNumber} holds a text no save that reported it sent");
            if (result.VersionPublished)
            {
                if (!version.IsPublished)
                    found.Add($"{o.Op} reported published, and its version is not");
                if (version.DefinitionJson != await desk.AsStoredAsync(o.Sent!))
                    found.Add($"{o.Op} reported published, and the published text is not the one it sent");
            }
            else if (o.Auto && Compiles(o.Sent!) != (result.Diagnostics.Count == 0))
                found.Add($"{o.Op} unpublished with {result.Diagnostics.Count} diagnostics for a text that compiles: {Compiles(o.Sent!)}");
            if (!o.Auto && Compiles(o.Sent!) && result.Diagnostics.Count > 0)
                found.Add($"{o.Op} reported diagnostics for a text that compiles");
        }
        foreach (var o in outcomes.Where(o => o.Op is "publish" or "rollback" && o.Result is not null))
            if (versions.SingleOrDefault(v => v.Id == o.Result!.VersionId) is not { IsPublished: true })
                found.Add($"{o.Op} reported a version that is not stored published");
        return found;
    }
}
