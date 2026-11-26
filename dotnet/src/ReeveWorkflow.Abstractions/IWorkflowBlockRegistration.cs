namespace ReeveWorkflow.Abstractions;

public interface IWorkflowBlockRegistration
{
    IReadOnlyCollection<IWorkflowBlockFactory> GetWorkflowBlockFactories();
}
