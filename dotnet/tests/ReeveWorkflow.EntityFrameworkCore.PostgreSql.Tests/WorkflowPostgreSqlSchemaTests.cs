using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;

namespace ReeveWorkflow.EntityFrameworkCore.PostgreSql.Tests;

public sealed class WorkflowPostgreSqlSchemaTests
{
    [Fact]
    public void The_shipped_migration_covers_every_change_in_the_model()
    {
        using var context = new WorkflowDesignTimeDbContextFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().Should().BeFalse(
            "a model change with no migration leaves every database one column short of what the code reads");
    }

    [Fact]
    public void A_context_on_another_provider_is_refused_rather_than_given_postgres_migrations()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflows(options => options.ConfigureDbContext(builder => builder.UseSqlite("Data Source=:memory:")))
            .AddPostgreSqlSchema();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolve = () => scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*needs the workflow context on Npgsql*");
    }

    [Fact]
    public void A_later_configurator_that_takes_the_migrations_assembly_back_is_refused_instead_of_migrating_the_wrong_chain()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflows(options => options.ConfigureDbContext(builder => builder
                .UseNpgsql("Host=workflow-test.invalid").EnableServiceProviderCaching(false)))
            .AddPostgreSqlSchema()
            .ConfigureDbContext(builder => builder.UseNpgsql("Host=workflow-test.invalid", npgsql => npgsql.MigrationsAssembly("Some.Host")));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var use = () => scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        use.Should().Throw<InvalidOperationException>().WithMessage("*Some.Host*AddPostgreSqlSchema*");
    }
}
