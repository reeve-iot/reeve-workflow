using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class WorkflowListingOnPostgresTests(ClusterBackends backends)
{
    private static readonly (string? Inputs, bool Has)[] Lists =
    [
        (null, false),
        ("[]", false),
        ("""[{"name":"a","type":"Int"}]""", true),
        ("""[{"name":"a","type":"List<Int"}]""", true),
        ("""[{"Name":"a","Type":"Int"}]""", false),
        ("""[{"name":"a","type":5}]""", false),
    ];

    [Fact]
    public async Task A_list_has_parameters_when_the_reader_reads_an_entry_from_it()
    {
        var connectionString = await backends.CreateDatabaseAsync("dispatch_listing_parameters");
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddWorkflows(workflows =>
        {
            workflows.ConfigureDbContext(context => context.UseNpgsql(connectionString));
        }).AddPostgreSqlSchema();
        await using var services = collection.BuildServiceProvider();
        var owner = Guid.NewGuid();
        var ids = new List<Guid>();
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            await db.Database.MigrateAsync();
            foreach (var (inputs, _) in Lists)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                db.Workflows.Add(new Workflow(id, $"w{ids.Count}", null, owner, DateTimeOffset.UtcNow));
            }
            await db.SaveChangesAsync();
            var versions = ids.Select((id, index) => new WorkflowVersion(
                Guid.NewGuid(), id, 1, "{\"nodes\":[],\"edges\":[]}", Lists[index].Inputs, Lists[index].Inputs,
                owner, DateTimeOffset.UtcNow, isPublished: true)).ToArray();
            db.WorkflowVersions.AddRange(versions);
            await db.SaveChangesAsync();
            for (var index = 0; index < ids.Count; index++)
                await db.Database.ExecuteSqlAsync($"UPDATE automation.workflows SET current_version_id={versions[index].Id} WHERE id={ids[index]}");
        }

        await using var read = services.CreateAsyncScope();
        var summaries = (await read.ServiceProvider.GetRequiredService<IWorkflowCatalog>().GetWorkflowsAsync(owner)).ToDictionary(s => s.Id);

        ids.Select(id => (summaries[id].HasInputParameters, summaries[id].HasOutputParameters))
            .Should().Equal(Lists.Select(l => (l.Has, l.Has)));
    }
}
