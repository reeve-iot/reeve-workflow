namespace ReeveWorkflow.Abstractions;

/// <summary>The JSON shape a literal of a registered type takes: the library's grading a literal is held to before a
/// host's own check sees it. The set is closed.</summary>
public enum WorkflowLiteralKind
{
    /// <summary>A JSON string.</summary>
    String,

    /// <summary>JSON <c>true</c> or <c>false</c>.</summary>
    Boolean,

    /// <summary>A JSON number with an integral value in the range of a 64-bit signed integer.</summary>
    Int,

    /// <summary>A finite JSON number.</summary>
    Number,

    /// <summary>Any JSON value but a top-level <c>null</c>: an object, an array, a string, a number or a Boolean.</summary>
    Json,
}

/// <summary>A further reading of a string literal, applied after it is known to be a JSON string.</summary>
public enum WorkflowLiteralFormat
{
    /// <summary>Thirty-six characters in the groups 8-4-4-4-12 of hexadecimal digits of either case.</summary>
    Uuid,

    /// <summary>A string the <see cref="DateTimeOffset"/> JSON converter reads.</summary>
    DateTime,
}

/// <summary>What a type declares about the literals written for it: how the JSON value of a literal is graded, and for a
/// string how its text is read further. A type with no form has no literal, and every literal of it is refused.</summary>
/// <remarks>Set it as <c>Literal</c> on a <see cref="WorkflowTypeRecord"/> or a <see cref="WorkflowTypeConstructorRecord"/>.
/// The library grades the shape; whether the value means anything to the host stays the host's own check.</remarks>
/// <param name="GradedAs">The JSON shape a literal must have.</param>
/// <param name="Format">The reading of a string literal; only with <see cref="WorkflowLiteralKind.String"/>.</param>
public sealed record WorkflowLiteralForm(WorkflowLiteralKind GradedAs, WorkflowLiteralFormat? Format = null)
{
    /// <summary>Why this form cannot be registered, or null when it can: a member outside the closed set, or a
    /// <see cref="Format"/> on a form that is not graded as <see cref="WorkflowLiteralKind.String"/>.</summary>
    public string? Refusal()
    {
        if (!Enum.IsDefined(GradedAs))
            return $"its literal form is graded as {(int)GradedAs}, which is outside String, Boolean, Int, Number and Json";
        if (Format is { } format && !Enum.IsDefined(format))
            return $"its literal format {(int)format} is outside Uuid and DateTime";
        if (Format is not null && GradedAs != WorkflowLiteralKind.String)
            return $"its literal format {Format} applies only to a form graded as String, not {GradedAs}";
        return null;
    }

    /// <summary>The form as <c>GET port-types</c> writes it: <c>String</c>, or <c>String/Uuid</c>.</summary>
    public override string ToString() => Format is null ? GradedAs.ToString() : $"{GradedAs}/{Format}";
}
