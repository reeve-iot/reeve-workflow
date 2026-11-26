using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Blocks;

public static class StandardWorkflowBlocksServiceCollectionExtensions
{
    private static readonly (string BlockTypeId, Func<IWorkflowBlockFactory> Create)[] Standard =
    [
        ("IfElse", () => new IfElseBlockFactory()),
        ("WhileLoop", () => new WhileLoopBlockFactory()),
        ("ForLoop", () => new ForLoopBlockFactory()),
        ("IterateOver", () => new IterateOverBlockFactory()),
        ("IfPresent", () => new IfPresentBlockFactory()),
        ("Coalesce", () => new CoalesceBlockFactory()),
        ("TryCatch", () => new TryCatchBlockFactory()),
        ("Rethrow", () => new RethrowBlockFactory()),
        ("Expression", () => new ExpressionBlockFactory()),
        ("Filter", () => new FilterBlockFactory()),
        ("Math", () => new MathBlockFactory()),
        ("ParseJson", () => new ParseJsonBlockFactory()),
        ("WaitForDuration", () => new WaitForDurationBlockFactory()),
        ("StringToInt", () => new StringToIntBlockFactory()),
        ("IntToString", () => new IntToStringBlockFactory()),
        ("StringToBool", () => new StringToBoolBlockFactory()),
        ("BoolToString", () => new BoolToStringBlockFactory()),
        ("NumberToString", () => new NumberToStringBlockFactory()),
        ("StringToNumber", () => new StringToNumberBlockFactory()),
        ("JsonToText", () => new JsonToTextBlockFactory()),
        ("TextToJson", () => new TextToJsonBlockFactory()),
        ("HttpRequest", () => new HttpRequestBlockFactory()),
        ("SubWorkflow", () => new SubWorkflowBlockFactory()),
    ];

    /// <summary>Registers the standard blocks as <see cref="IWorkflowBlockFactory"/> singletons, which is where
    /// a block catalogue reads them from. Opt-in: a host that wants none of them calls nothing.</summary>
    /// <param name="services">The host's service collection; the factories are added to it.</param>
    /// <param name="configure">Names the ids this host replaces with its own factory — see
    /// <see cref="StandardWorkflowBlockOptions.Override{TFactory}"/>. Leave unset to take all of them as they ship.</param>
    public static IServiceCollection AddStandardWorkflowBlocks(
        this IServiceCollection services,
        Action<StandardWorkflowBlockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new StandardWorkflowBlockOptions(
            Standard.Select(entry => entry.BlockTypeId).ToHashSet(StringComparer.OrdinalIgnoreCase));
        configure?.Invoke(options);

        foreach (var (blockTypeId, create) in Standard)
        {
            if (options.TryGetReplacement(blockTypeId, out var replacement))
                services.AddSingleton<IWorkflowBlockFactory>(sp => Build(sp, replacement, blockTypeId));
            else
                services.AddSingleton<IWorkflowBlockFactory>(_ => create());
        }

        return services;
    }

    private static IWorkflowBlockFactory Build(IServiceProvider services, Type replacement, string blockTypeId)
    {
        var factory = (IWorkflowBlockFactory)ActivatorUtilities.CreateInstance(services, replacement);

        if (!string.Equals(factory.BlockTypeId, blockTypeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{replacement.FullName}' was registered as the replacement for block type id '{blockTypeId}', "
                + $"but it declares BlockTypeId '{factory.BlockTypeId}'. Taking its own id would add a block "
                + $"nothing references and leave '{blockTypeId}' with none.");
        }

        return factory;
    }
}
