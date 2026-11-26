using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ReeveWorkflow.DependencyInjection;

namespace ReeveWorkflow.Editor;

/// <summary>Registers the Editor. It hangs off the same builder every optional part of the library does,
/// and the Executor's own registration knows nothing about it.</summary>
public static class WorkflowEditorServiceCollectionExtensions
{
    /// <summary>Adds the write store and the seam for the transaction it writes in. Scoped, because the
    /// context it writes through is: a singleton editor would hold one request's unit of work forever.</summary>
    public static IWorkflowBuilder AddWorkflowEditor(
        this IWorkflowBuilder builder, Action<WorkflowEditorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new WorkflowEditorOptions();
        configure?.Invoke(options);

        options.Transaction.Register(builder.Services);
        builder.Services.TryAddScoped<IWorkflowEditor, WorkflowEditor>();
        builder.Services.TryAddScoped<WorkflowCompilationReads>();
        builder.Services.TryAddScoped<WorkflowDraftAnalyses>();

        return builder;
    }
}

/// <summary>What a host says about the Editor. One slot today: whose transaction a write is made in.</summary>
public sealed class WorkflowEditorOptions
{
    /// <summary>Where the host puts its own unit of work, so an import writes the host's rows and the
    /// library's as one intent. Unfilled, the library opens a transaction on its own context.</summary>
    public WorkflowSeam<IWorkflowEditorTransaction> Transaction { get; } = new(
        ServiceDescriptor.Scoped<IWorkflowEditorTransaction, WorkflowContextEditorTransaction>());
}
