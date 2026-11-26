using Microsoft.Extensions.DependencyInjection;

namespace ReeveWorkflow.DependencyInjection;

/// <summary>What <c>AddWorkflows(...)</c> returns, and the type every optional part of the library hangs its own <c>AddX()</c> on.</summary>
/// <remarks>It lives in a package of its own so that a block author's dependency closure stays at exactly one package and the engine package stays free of dependency injection — the cost is one package to version forever, paid to keep that claim true.</remarks>
public interface IWorkflowBuilder
{
    /// <summary>The collection the add-on registers into.</summary>
    IServiceCollection Services { get; }
}

/// <summary>The one implementation, so an add-on never has to supply its own.</summary>
public sealed class WorkflowBuilder(IServiceCollection services) : IWorkflowBuilder
{
    /// <inheritdoc />
    public IServiceCollection Services { get; } =
        services ?? throw new ArgumentNullException(nameof(services));
}
