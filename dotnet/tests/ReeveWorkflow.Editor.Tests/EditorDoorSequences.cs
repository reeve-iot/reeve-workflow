using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public static partial class EditorDoorSequences
{
    public static readonly string[] Steps = ["update", "draft", "gate", "publish", "rollback", "delete", "settings", "track", "peer"];

    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";

    private static string Text(int step) =>
        $$$"""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"u{{{step}}}","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"u{{{step}}}","mappings":{}}]}""";

    public static IEnumerable<string[]> Exhaustive(int length)
    {
        IEnumerable<string[]> sequences = [[]];
        for (var i = 0; i < length; i++)
            sequences = from sequence in sequences from step in Steps select sequence.Append(step).ToArray();
        return sequences;
    }

    public static IEnumerable<string[]> Random(int count, int length, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
            yield return Enumerable.Range(0, length).Select(_ => Steps[random.Next(Steps.Length)]).ToArray();
    }

    public static async Task<List<string>> DifferingAsync(IServiceProvider services, Guid owner, IEnumerable<string[]> all)
    {
        var differing = new List<string>();
        foreach (var sequence in all)
        {
            var perStep = await RunAsync(services, owner, sequence, oneScope: false);
            var oneScope = await RunAsync(services, owner, sequence, oneScope: true);
            if (oneScope != perStep)
                differing.Add($"{string.Join(",", sequence)}\n  per step: {perStep}\n  one scope: {oneScope}");
        }

        return differing;
    }

    public static async Task<string> RunAsync(IServiceProvider services, Guid owner, string[] sequence, bool oneScope)
    {
        Guid id;
        await using (var first = services.CreateAsyncScope())
            id = (await first.ServiceProvider.GetRequiredService<IWorkflowEditor>().CreateAsync(owner, new CreateWorkflowRequest("Ship", null, Valid))).Workflow.Id;
        await using (var second = services.CreateAsyncScope())
            await second.ServiceProvider.GetRequiredService<IWorkflowEditor>().UpdateAsync(owner, id,
                new UpdateWorkflowRequest("Ship", null, Text(0), AutoPublish: false));

        var answers = new List<string>();
        await using var shared = services.CreateAsyncScope();
        for (var i = 0; i < sequence.Length; i++)
        {
            if (sequence[i] == "peer")
            {
                await using var peer = services.CreateAsyncScope();
                answers.Add("peer: " + await StepAsync(services, peer.ServiceProvider, owner, "draft", id, 100 + i));
            }
            else if (oneScope)
                answers.Add(sequence[i] + ": " + await StepAsync(services, shared.ServiceProvider, owner, sequence[i], id, i + 1));
            else
            {
                await using var own = services.CreateAsyncScope();
                answers.Add(sequence[i] + ": " + await StepAsync(services, own.ServiceProvider, owner, sequence[i], id, i + 1));
            }
        }

        answers.Add(await StateAsync(services, id));
        return string.Join(" | ", answers);
    }

    private static async Task<string> StepAsync(IServiceProvider services, IServiceProvider scope, Guid owner, string step, Guid id, int n)
    {
        var editor = scope.GetRequiredService<IWorkflowEditor>();
        try
        {
            var result = step switch
            {
                "update" => await editor.UpdateAsync(owner, id, new UpdateWorkflowRequest("Ship", null, Text(n))),
                "draft" => await editor.UpdateAsync(owner, id, new UpdateWorkflowRequest("Ship", null, Text(n), AutoPublish: false)),
                "gate" => await RepeatAsync(services, editor, owner, id, "Gate", autoPublish: true),
                "publish" => await editor.PublishAsync(owner, id),
                "rollback" => await editor.RollbackAsync(owner, id, 1),
                "delete" => await editor.DeleteAsync(owner, id),
                "settings" => await RepeatAsync(services, editor, owner, id, $"Renamed{n}", autoPublish: false),
                "track" => null,
                _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
            };
            if (result is null)
            {
                var workflow = await scope.GetRequiredService<WorkflowDbContext>().Workflows
                    .Include(w => w.CurrentVersion).Include(w => w.DraftVersion).SingleOrDefaultAsync(w => w.Id == id);
                return workflow is null ? "none" : "tracked";
            }

            return $"{result.Workflow.Name} v{result.VersionNumber?.ToString() ?? "-"} published={result.VersionPublished} "
                + $"current={result.Workflow.CurrentVersionNumber?.ToString() ?? "-"} draft={result.Workflow.DraftVersionNumber?.ToString() ?? "-"} "
                + $"fault={result.Fault?.GetType().Name ?? "-"} diagnostics={result.Diagnostics.Count}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"throws {exception.GetType().Name}";
        }
    }

    private static async Task<WorkflowEditResult> RepeatAsync(
        IServiceProvider services, IWorkflowEditor editor, Guid owner, Guid id, string name, bool autoPublish)
    {
        string definition = Valid;
        string? inputs = null, outputs = null;
        await using (var scope = services.CreateAsyncScope())
        {
            var workflow = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Workflows.AsNoTracking()
                .Include(w => w.CurrentVersion).Include(w => w.DraftVersion).SingleOrDefaultAsync(w => w.Id == id);
            if ((workflow?.DraftVersion ?? workflow?.CurrentVersion) is { } version)
                (definition, inputs, outputs) = (version.DefinitionJson, version.InputParametersJson, version.OutputParametersJson);
        }

        return await editor.UpdateAsync(owner, id, new UpdateWorkflowRequest(name, null, definition, inputs, outputs, AutoPublish: autoPublish));
    }

    private static async Task<string> StateAsync(IServiceProvider services, Guid id)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = await db.Workflows.AsNoTracking().Include(w => w.CurrentVersion).Include(w => w.DraftVersion)
            .SingleOrDefaultAsync(w => w.Id == id);
        if (workflow is null)
            return "row: none";
        var versions = (await db.WorkflowVersions.AsNoTracking().Where(v => v.WorkflowId == id).OrderBy(v => v.VersionNumber)
                .Select(v => new { v.VersionNumber, v.IsPublished, v.DefinitionJson }).ToListAsync())
            .Select(v => $"{v.VersionNumber}{(v.IsPublished ? "p" : "d")}{Marker().Match(v.DefinitionJson).Value}");
        return $"row: {workflow.Name} r{workflow.Revision} current={workflow.CurrentVersion?.VersionNumber.ToString() ?? "-"} "
            + $"draft={workflow.DraftVersion?.VersionNumber.ToString() ?? "-"} versions={string.Join(",", versions)}";
    }

    [GeneratedRegex(@"u\d+")]
    private static partial Regex Marker();
}
