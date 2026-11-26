using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Blocks;

/// <summary>Applies <c>Op</c> to <c>A</c> and <c>B</c> as <c>double</c> arithmetic, and answers a <c>Number</c> for every <c>Op</c>.
/// A division or modulo by zero and a non-finite result fail the node.</summary>
public sealed class MathBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        var op = BlockInputCoercion.String(inputs, "Op") ?? "Add";
        var value = NumberResult(op, Operand(inputs, "A"), Operand(inputs, "B"));

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["Result"] = value });
    }

    private static double NumberResult(string op, double a, double b)
    {
        var result = op switch
        {
            "Add" => a + b,
            "Sub" => a - b,
            "Mul" => a * b,
            "Div" => b == 0 ? throw new DivideByZeroException("Division by zero.") : a / b,
            "Mod" => b == 0 ? throw new DivideByZeroException("Modulo by zero.") : a % b,
            "Pow" => Math.Pow(a, b),
            "Min" => Math.Min(a, b),
            "Max" => Math.Max(a, b),
            _ => throw new ArgumentException($"Unsupported operation: '{op}'."),
        };

        return double.IsFinite(result)
            ? result
            : throw new OverflowException($"The result of '{op}' on {a} and {b} is not a finite number.");
    }

    private static double Operand(IReadOnlyDictionary<string, object?> inputs, string input) =>
        inputs.GetValueOrDefault(input) switch
        {
            long whole => whole,
            double number => number,
            _ => throw new ArgumentException($"Input '{input}' is required and must be a number."),
        };
}

public sealed class MathBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "Math";
    public string DisplayName => "Math";
    public string Description => "Performs a mathematical operation on two numbers and answers a Number.";
    public string Category => "Math";
    /// <summary>Arithmetic over its two inputs.</summary>
    public bool IsIdempotent => true;

    /// <summary><c>$T</c> is the operands' term, bound by what feeds <c>A</c> and <c>B</c>.</summary>
    public IReadOnlyList<WorkflowTypeParameter> TypeParameters =>
        [new("T", [TypeParameterConstraints.Numeric])];

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("A", "A", PortValueType.OfDeclaration("$T"), true),
        new BlockPortDefinition("B", "B", PortValueType.OfDeclaration("$T"), true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Result", "Result", PortValueType.Number, false)
    ];

    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new BlockConfigFieldDefinition("A", "A", ConfigFieldType.Number,
            Required: false, Description: "Left operand: an integer or a number, or an expression."),
        new BlockConfigFieldDefinition("B", "B", ConfigFieldType.Number,
            Required: false, Description: "Right operand: an integer or a number, or an expression."),
        new BlockConfigFieldDefinition("Op", "Operation", ConfigFieldType.Enum,
            Required: false, DefaultValue: "Add", Description: "Operation to perform.")
        {
            AllowedValues = ["Add", "Sub", "Mul", "Div", "Mod", "Pow", "Min", "Max"],
        }
    ];

    public IWorkflowBlockPlugin Create() => new MathBlock();
}
