using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Blocks;

public sealed class WaitForDurationBlock : IWorkflowBlockPlugin
{
    private const long MaxDelayMilliseconds = uint.MaxValue - 1;

    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        var durationMs = BlockInputCoercion.Int(inputs, "DurationMs")
            ?? throw new ArgumentException("Input 'DurationMs' is required.");
        if (durationMs < 0)
        {
            throw new ArgumentException("DurationMs must be greater than or equal to 0.");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(BlockInputCoercion.Within(durationMs, MaxDelayMilliseconds, "DurationMs")), cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, object?>
        {
            ["Completed"] = true,
            ["DurationMs"] = durationMs
        };
    }

}

public sealed class WaitForDurationBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "WaitForDuration";
    public string DisplayName => "Wait For Duration";
    public string Description => "Waits for a configured duration before continuing the workflow.";
    public string Category => "Control Flow";
    /// <summary>A delay leaves nothing behind; repeating it costs time and nothing else.</summary>
    public bool IsIdempotent => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("DurationMs", "Duration (ms)", PortValueType.Int, true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Completed", "Completed", PortValueType.Boolean, false),
        new BlockPortDefinition("DurationMs", "Duration (ms)", PortValueType.Int, false)
    ];

    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new BlockConfigFieldDefinition("DurationMs", "Duration (ms)", ConfigFieldType.Int,
            Required: false, DefaultValue: "1000",
            Description: "How long to wait before the next block executes.")
    ];

    public IWorkflowBlockPlugin Create() => new WaitForDurationBlock();
}
