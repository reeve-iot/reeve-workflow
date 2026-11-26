using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Tests.Hosting;

public sealed class InProcessRestartGenerationsTests : RestartGenerationsContract
{
    private InProcessRestartGenerations? shared;

    protected override IWorkflowRestartGenerations Store() => shared ??= new InProcessRestartGenerations(Clock);
}
