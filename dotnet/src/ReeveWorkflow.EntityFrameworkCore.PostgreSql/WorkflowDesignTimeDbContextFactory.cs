using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ReeveWorkflow.EntityFrameworkCore.PostgreSql;

/// <summary>Hands <c>dotnet ef</c> a context it can build with no host. It exists for the tool and is not a
/// composition seam: a host never calls it.</summary>
/// <remarks>Scaffolding reads the model, not the database, so the connection string is never dialled — and names a
/// host that cannot resolve, so a tool that did try to connect fails loudly instead of finding whatever
/// PostgreSQL the machine runs.</remarks>
public sealed class WorkflowDesignTimeDbContextFactory : IDesignTimeDbContextFactory<WorkflowDbContext>
{
    private const string NeverDialled = "Host=workflow-design-time.invalid;Database=workflow;Username=none;Password=none";

    public WorkflowDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<WorkflowDbContext>().UseNpgsql(NeverDialled);
        builder.UseWorkflowPostgreSqlSchema();
        return new WorkflowDbContext(builder.Options);
    }
}
