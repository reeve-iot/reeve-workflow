using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public static class EditorDoorPairs
{
    public static readonly string[] Doors = ["create", "update", "publish", "rollback", "delete", "settings"];

    public static IEnumerable<object[]> Pairs => from first in Doors from second in Doors select new object[] { first, second };

    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";

    private static string Text(int step) =>
        $$$"""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"u{{{step}}}","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"u{{{step}}}","mappings":{}}]}""";

    public static async Task<string> RunAsync(IServiceProvider services, Guid owner, string first, string second, bool oneScope)
    {
        var id = first == "create" ? (Guid?)null : await SetUpAsync(services, owner);
        var answers = new List<string>();
        if (oneScope)
        {
            await using var scope = services.CreateAsyncScope();
            var editor = scope.ServiceProvider.GetRequiredService<IWorkflowEditor>();
            foreach (var (door, step) in new[] { (first, 1), (second, 2) })
                id = await DoorAsync(services, editor, owner, door, id, step, answers);
        }
        else
        {
            foreach (var (door, step) in new[] { (first, 1), (second, 2) })
            {
                await using var scope = services.CreateAsyncScope();
                id = await DoorAsync(services, scope.ServiceProvider.GetRequiredService<IWorkflowEditor>(), owner, door, id, step, answers);
            }
        }

        answers.Add(await StateAsync(services, id));
        return string.Join(" | ", answers);
    }

    private static async Task<Guid> SetUpAsync(IServiceProvider services, Guid owner)
    {
        await using var scope = services.CreateAsyncScope();
        var editor = scope.ServiceProvider.GetRequiredService<IWorkflowEditor>();
        var created = await editor.CreateAsync(owner, new CreateWorkflowRequest("Ship", null, Valid));
        await using var second = services.CreateAsyncScope();
        await second.ServiceProvider.GetRequiredService<IWorkflowEditor>().UpdateAsync(owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, Text(0), AutoPublish: false));
        return created.Workflow.Id;
    }

    private static async Task<Guid?> DoorAsync(
        IServiceProvider services, IWorkflowEditor editor, Guid owner, string door, Guid? id, int step, List<string> answers)
    {
        try
        {
            WorkflowEditResult result;
            switch (door)
            {
                case "create":
                    result = await editor.CreateAsync(owner, new CreateWorkflowRequest("Made", null, Text(step)));
                    id ??= result.Workflow.Id;
                    break;
                case "update":
                    result = await editor.UpdateAsync(owner, id!.Value, new UpdateWorkflowRequest("Ship", null, Text(step)));
                    break;
                case "publish":
                    result = await editor.PublishAsync(owner, id!.Value);
                    break;
                case "rollback":
                    result = await editor.RollbackAsync(owner, id!.Value, 1);
                    break;
                case "delete":
                    result = await editor.DeleteAsync(owner, id!.Value);
                    break;
                case "settings":
                    var texts = await TextsAsync(services, id!.Value);
                    result = await editor.UpdateAsync(owner, id.Value, new UpdateWorkflowRequest(
                        $"Renamed{step}", null, texts.Definition, texts.Inputs, texts.Outputs, AutoPublish: false));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(door), door, null);
            }

            answers.Add($"{door}: {result.Workflow.Name} v{result.VersionNumber?.ToString() ?? "-"} published={result.VersionPublished} "
                + $"current={result.Workflow.CurrentVersionNumber?.ToString() ?? "-"} draft={result.Workflow.DraftVersionNumber?.ToString() ?? "-"} "
                + $"fault={result.Fault?.GetType().Name ?? "-"}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            answers.Add($"{door}: throws {exception.GetType().Name}");
        }

        return id;
    }

    private static async Task<(string Definition, string? Inputs, string? Outputs)> TextsAsync(IServiceProvider services, Guid id)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = await db.Workflows.AsNoTracking().Include(w => w.CurrentVersion).Include(w => w.DraftVersion)
            .SingleOrDefaultAsync(w => w.Id == id);
        var version = workflow?.DraftVersion ?? workflow?.CurrentVersion;
        return version is null ? (Valid, null, null) : (version.DefinitionJson, version.InputParametersJson, version.OutputParametersJson);
    }

    private static async Task<string> StateAsync(IServiceProvider services, Guid? id)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = await db.Workflows.AsNoTracking().Include(w => w.CurrentVersion).Include(w => w.DraftVersion)
            .SingleOrDefaultAsync(w => w.Id == id);
        if (workflow is null)
            return "row: none";
        var versions = await db.WorkflowVersions.AsNoTracking().Where(v => v.WorkflowId == workflow.Id)
            .OrderBy(v => v.VersionNumber).Select(v => v.VersionNumber + (v.IsPublished ? "p" : "d")).ToListAsync();
        return $"row: {workflow.Name} r{workflow.Revision} current={workflow.CurrentVersion?.VersionNumber.ToString() ?? "-"} "
            + $"draft={workflow.DraftVersion?.VersionNumber.ToString() ?? "-"} versions={string.Join(",", versions)}";
    }
}
