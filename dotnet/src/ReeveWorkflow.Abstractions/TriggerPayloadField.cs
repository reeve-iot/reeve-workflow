namespace ReeveWorkflow.Abstractions;

/// <summary>One leaf of the fire envelope a trigger type promises to populate — what the expression editor
/// offers under <c>${trigger.*}</c> for a workflow bound to that trigger.</summary>
public sealed record TriggerPayloadField(string Name, string Type, string Description)
{
    /// <summary>The identifier of the automation trigger itself, written last by the host. Constant across
    /// every fire of that trigger, so it tells two executions apart from nothing. A trigger must not declare it.</summary>
    public const string ReservedId = "id";

    /// <summary>The fire instant the host writes last. A trigger must not declare it.</summary>
    public const string ReservedFiredAt = "firedAt";

    /// <summary>Keys the host owns, set AFTER the plugin's payload is spread — a plugin-supplied value for
    /// either is dropped rather than believed, so a plugin cannot misattribute a fire.</summary>
    public static IReadOnlyCollection<string> ReservedNames { get; } = [ReservedId, ReservedFiredAt];

    /// <summary>What an operator is shown beside <see cref="ReservedId"/> in the expression editor. Declared
    /// here so the sentence and the value it describes cannot drift apart.</summary>
    public static string ReservedIdDescription { get; } =
        "Identifier of the automation trigger that fired, the same value on every fire of that trigger — "
        + "it does not tell two executions apart. The persisted TriggerData port carries it again as triggerId.";

    /// <summary>What an operator is shown beside <see cref="ReservedFiredAt"/>. Same reason as
    /// <see cref="ReservedIdDescription"/>.</summary>
    public static string ReservedFiredAtDescription { get; } =
        "Instant the trigger fired, written by the host on every fire.";
}
