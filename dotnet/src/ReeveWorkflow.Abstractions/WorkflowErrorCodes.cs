namespace ReeveWorkflow.Abstractions;

/// <summary>The library's values of <c>_error.code</c>. A host's coded exception (<see cref="IWorkflowCodedException"/>)
/// puts its own code there; these are the ones the library raises.</summary>
public static class WorkflowErrorCodes
{
    /// <summary>A value a producer wrote, or a container handed its sub-canvas, is off the port's term: null on a non-nullable
    /// term, a value of another CLR type, a <c>Json</c> value that is a JSON null, or a present value that serialises to JSON
    /// <c>null</c> on a non-null <c>Json</c> output port.</summary>
    public const string BlockOutputTypeMismatch = "block-output-type-mismatch";

    /// <summary>A block's own throw, a timeout, a child that failed: any exception that names no code.</summary>
    public const string BlockFailed = "block-failed";

    /// <summary>An Enum configuration field held a value outside its names.</summary>
    public const string ConfigValueNotAllowed = "config_value_not_allowed";

    /// <summary>An expression failed while it ran.</summary>
    public const string ExpressionRuntimeError = "expression-runtime-error";

    /// <summary>A projection wire found nothing for a term that needs a value (category <c>value-absent</c>), a node that
    /// is not a value of its term (category <c>narrowing</c>), or a path past the JSONPath evaluation budget (category
    /// <c>too-large</c>).</summary>
    public const string JsonProjectionFailed = "json-projection-failed";

    /// <summary>A required input held no value when its node was about to run.</summary>
    public const string RequiredInputValueAbsent = "required-input-value-absent";

    /// <summary>A protected or referenced entry of a secret-capable input could not be revealed, so the block never sent it. The message
    /// names the input and the key, never the value.</summary>
    public const string SecretUnresolved = "secret-unresolved";

    /// <summary>The executor refused a park: its config names no <c>Expression&lt;Boolean&gt;</c> input of the parked node, or a
    /// <c>Predicate</c> park's block does not build its resumed outputs.</summary>
    public const string SuspendConfigInvalid = "suspend-config-invalid";
}
