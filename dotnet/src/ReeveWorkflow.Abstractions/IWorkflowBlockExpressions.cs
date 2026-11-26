namespace ReeveWorkflow.Abstractions;

/// <summary>Evaluates the <c>Expression&lt;T&gt;</c> inputs of the node a block is running, which the engine hands the block as
/// unevaluated text (<see cref="WorkflowBlockContext.Expressions"/>).</summary>
public interface IWorkflowBlockExpressions
{
    /// <summary>Evaluates the typed tree the node's compile gave <paramref name="input"/>, in the node's scope with
    /// <paramref name="variables"/> bound to the scope variables the input declares
    /// (<see cref="BlockPortDefinition.ScopeVariables"/>). Call it any number of times while the block runs.</summary>
    /// <param name="input">The name of an <c>Expression&lt;T&gt;</c> input of the node.</param>
    /// <param name="variables">A value for each variable the input declares, by name. Each is checked against its declared term
    /// first, as a block's output is, and an <c>int</c> on an <c>Int</c> variable is read as a <c>long</c>.</param>
    /// <returns>The value, in the CLR type of the input's <c>T</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="input"/> is not an <c>Expression&lt;T&gt;</c> input of the node with an
    /// expression to evaluate, a declared variable is missing, a name is not one the input declares, or a value is not one of its
    /// variable's term. It is the block's defect: the node fails with <c>block-failed</c>.</exception>
    /// <exception cref="InvalidOperationException">The context was built outside a compiled run, which has nothing to evaluate.</exception>
    /// <exception cref="IWorkflowCodedException">The evaluation failed. The exception is Core's <c>ExpressionEvaluationException</c>:
    /// its code is <c>expression-runtime-error</c> and its category says why (<c>overflow</c>, <c>division-by-zero</c>,
    /// <c>narrowing</c>, <c>value-absent</c>, <c>too-large</c>), so the node fails with that code, not <c>block-failed</c>.</exception>
    object? Evaluate(string input, IReadOnlyDictionary<string, object?> variables);
}
