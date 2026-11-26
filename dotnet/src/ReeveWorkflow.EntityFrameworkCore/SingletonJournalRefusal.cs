using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore;

internal sealed class SingletonJournalRefusal(IServiceCollection services)
{
    private readonly Lazy<bool> refused = new(() => AnswersWithASingleton(services));

    public void ThrowIfASingletonAnswers()
    {
        if (refused.Value)
            throw Refusal();
    }

    public static void ThrowIfASingletonAnswers(IServiceCollection services)
    {
        if (AnswersWithASingleton(services))
            throw Refusal();
    }

    private static bool AnswersWithASingleton(IServiceCollection services) =>
        services.LastOrDefault(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IWorkflowRunJournal))
            ?.Lifetime == ServiceLifetime.Singleton;

    private static InvalidOperationException Refusal() => new(
        "The IWorkflowRunJournal that answers is a singleton: Seams.RunJournal filled with UseInstance or a singleton "
        + "lifetime, or a singleton IWorkflowRunJournal registered in the container, before or after AddWorkflows. "
        + "A journal holds the run it opened or loaded, because SaveRunAsync names no row, so one instance shared by "
        + "concurrent runs would save each run's changes with another's. Register it scoped: "
        + "Seams.RunJournal.Use<T>() or Seams.RunJournal.Use(factory), which are scoped when no lifetime is named, "
        + "or a scoped registration in the container.");
}
