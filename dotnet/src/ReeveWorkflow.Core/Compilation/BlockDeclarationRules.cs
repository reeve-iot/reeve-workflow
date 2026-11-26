using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Compilation;

internal static class BlockDeclarationRules
{
    private enum Position { Input, Output, SubCanvasInput, SubCanvasOutput, ScopeVariable }

    internal static DeclarationFault? Find(BlockShape shape)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var foldedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in shape.TypeParameters)
        {
            var name = parameter.Name ?? "";
            if (!PortTypeGrammar.IsIdentifier(name))
                return new($"type parameter '{name}'", "has a name that is not an identifier");
            if (!declared.Add(name))
                return new($"type parameter '{name}'", "is declared more than once");
            if (!foldedNames.Add(name))
                return new($"type parameter '{name}'", "differs only in case from another parameter, which a port type cannot tell apart");
            foreach (var constraint in parameter.Constraints)
            {
                if (!TypeParameterConstraints.Known.Contains(constraint, StringComparer.Ordinal))
                    return new($"type parameter '{name}'", $"is constrained by '{constraint}', which is not a capability");
            }
        }

        if (shape.Flags.DeclaredOutputsCollect && !shape.Flags.SupportsDeclaredOutputs)
            return new("DeclaredOutputsCollect", "is set on a block that does not support declared outputs");
        if (shape.Flags.DeclaredOutputsCollect && shape.SubCanvasNames.Count != 1)
            return new("DeclaredOutputsCollect", $"is set on a block with {shape.SubCanvasNames.Count} sub-canvases, and it collects across the calls of exactly one");

        var bodyBound = BodyBound(shape);
        foreach (var (branch, port, position) in DeclaredPorts(shape))
        {
            if ((GradePort(branch, port, position, declared, bodyBound) ?? GradeScopeVariables(port, declared, bodyBound, branch)
                    ?? GradeSecretValues(port, position, branch)) is { } fault)
                return fault;
        }

        return null;
    }

    private static IEnumerable<(string? Branch, PortShape Port, Position Position)> DeclaredPorts(BlockShape shape)
    {
        foreach (var port in shape.Inputs)
            yield return (null, port, Position.Input);
        foreach (var port in shape.Outputs)
            yield return (null, port, Position.Output);
        foreach (var branch in shape.SubCanvasNames)
        {
            foreach (var port in shape.SubCanvasInputs[branch])
                yield return (branch, port, Position.SubCanvasInput);
            foreach (var port in shape.SubCanvasOutputs[branch])
                yield return (branch, port, Position.SubCanvasOutput);
        }
    }

    internal static void Refuse(BlockShape shape)
    {
        if (Find(shape) is { } fault)
            throw Invalid(shape.BlockTypeId, fault.Subject, fault.Reason);
    }

    internal readonly record struct DeclarationFault(string Subject, string Reason);

    private static DeclarationFault? GradeScopeVariables(
        PortShape port, HashSet<string> declared, HashSet<string> bodyBound, string? branch = null)
    {
        if (port.ScopeVariables.Count == 0)
            return null;
        var where = branch is null ? $"port '{port.Name}'" : $"sub-canvas '{branch}' port '{port.Name}'";
        if (branch is not null || !port.Type.IsExpression)
        {
            return new(where,
                $"declares scope variables but has the type '{port.Type.Name}'; only an Expression<T> input of the block may");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in port.ScopeVariables)
        {
            var name = variable?.Name ?? "";
            var at = $"{where} scope variable '{name}'";
            if (!WorkflowExpressionNamespaces.IsScopeVariableName(name))
            {
                return new(at,
                    $"is not a scope-variable name; the library reserves {string.Join(" and ", WorkflowExpressionNamespaces.ScopeVariableNames.Select(n => $"'{n}'"))}");
            }

            if (!seen.Add(name))
                return new(at, "is declared more than once on the input");
            if (ParseVariable(variable!) is not { } term)
                return new(at, $"has the type '{variable!.Type}', which is not a declaration term");
            if (GradeTerm(at, variable!.Type, term, Position.ScopeVariable, declared, bodyBound) is { } fault)
                return fault;
        }

        return null;
    }

    private static DeclarationFault? GradeSecretValues(PortShape port, Position position, string? branch)
    {
        if (!port.SecretValues)
            return null;
        var where = branch is null ? $"port '{port.Name}'" : $"sub-canvas '{branch}' port '{port.Name}'";
        if (position != Position.Input)
            return new(where, "declares SecretValues but is not an input of the block; only an input may");
        return Parse(port) is { Kind: PortTermKind.Constructor, Name: "Map", Arguments: [{ Kind: PortTermKind.Atom, Name: "String", IsNullable: false }, { Kind: PortTermKind.Atom, Name: "String", IsNullable: false }] }
            ? null
            : new(where, $"declares SecretValues but has the type '{port.Type.Name}'; only Map<String,String> or Map<String,String>? may");
    }

    private static PortTerm? ParseVariable(ExpressionScopeVariable variable) =>
        PortTerm.TryParse(variable.Type, PortTermGrammar.Declaration, out var term, out _) ? term : null;

    internal static void RefuseReservedNames(BlockShape shape)
    {
        foreach (var (port, where) in AllPorts(shape))
        {
            if (Parse(port) is { } term && ReservedPortTypeNames.In(term) is { } reserved)
                throw new ArgumentException(
                    $"{CompilationRules.PortTypeAlias}: Block '{shape.BlockTypeId}' {where} has the type '{port.Type.Name}', "
                    + $"which holds {reserved}, a name that is not a port type.", "factory");
            foreach (var variable in port.ScopeVariables)
            {
                if (ParseVariable(variable) is { } variableTerm && ReservedPortTypeNames.In(variableTerm) is { } held)
                    throw new ArgumentException(
                        $"{CompilationRules.PortTypeAlias}: Block '{shape.BlockTypeId}' {where} scope variable '{variable.Name}' has the type "
                        + $"'{variable.Type}', which holds {held}, a name that is not a port type.", "factory");
            }
        }
    }

    internal static void RefuseVocabulary(BlockShape shape, PortTypeWideningTable table)
    {
        foreach (var fault in Ungraded(shape, table))
            throw new ArgumentException($"{fault.Rule}: Block '{shape.BlockTypeId}' {fault.Where} has the type '{fault.Term}'{fault.What}.", "factory");
    }

    internal readonly record struct DeclaredFault(string Where, string PortName, PortTerm Term, string Rule, string What);

    internal static IEnumerable<DeclaredFault> Ungraded(BlockShape shape, PortTypeWideningTable table)
    {
        foreach (var (port, where) in AllPorts(shape))
        {
            if (Fault(where, port.Name, Parse(port), table) is { } fault)
                yield return fault;
            foreach (var variable in port.ScopeVariables)
            {
                if (Fault($"{where} scope variable '{variable.Name}'", port.Name, ParseVariable(variable), table) is { } variableFault)
                    yield return variableFault;
            }
        }
    }

    internal static PortTerm Graded(PortTerm term) =>
        term is { Kind: PortTermKind.Constructor, Name: "Expression", Arguments: [var argument] } ? argument : term;

    private static DeclaredFault? Fault(string where, string portName, PortTerm? term, PortTypeWideningTable table)
    {
        if (term is null)
            return null;
        term = Graded(term);
        return PortTypeNameSites.Unspelled(term.ToString(), term, table) is { } fault
            ? new DeclaredFault(where, portName, term, fault.Rule, fault.What)
            : null;
    }

    private static IEnumerable<(PortShape Port, string Where)> AllPorts(BlockShape shape)
    {
        foreach (var port in shape.Inputs.Concat(shape.Outputs))
            yield return (port, $"port '{port.Name}'");
        foreach (var branch in shape.SubCanvasNames)
        {
            foreach (var port in shape.SubCanvasInputs[branch].Concat(shape.SubCanvasOutputs[branch]))
                yield return (port, $"sub-canvas '{branch}' port '{port.Name}'");
        }
    }

    internal static HashSet<string> BodyBound(BlockShape shape)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in shape.SubCanvasNames)
        {
            foreach (var port in shape.SubCanvasOutputs[branch])
            {
                for (var term = Parse(port); term is not null; term = term.LastArgument)
                    if (term.Kind == PortTermKind.Parameter)
                        names.Add(term.Name);
            }
        }

        return names;
    }

    private static PortTerm? Parse(PortShape port) =>
        PortTerm.TryParse(port.Type.Name, PortTermGrammar.Declaration, out var term, out _) ? term : null;

    private static DeclarationFault? GradePort(
        string? branch, PortShape port, Position position, HashSet<string> declared, HashSet<string> bodyBound)
    {
        var where = branch is null ? $"port '{port.Name}'" : $"sub-canvas '{branch}' port '{port.Name}'";
        return Parse(port) is { } term
            ? GradeTerm(where, port.Type.Name, term, position, declared, bodyBound)
            : new(where, $"has the type '{port.Type.Name}', which is not a declaration term");
    }

    private static DeclarationFault? GradeTerm(
        string where, string type, PortTerm term, Position position, HashSet<string> declared, HashSet<string> bodyBound)
    {
        if (ReservedPortTypeNames.In(term) is { } reserved)
            return new(where, $"has the type '{type}', which holds {reserved}, a name that is not a port type");
        var isOutput = position is Position.Output or Position.SubCanvasOutput;
        var insideExpression = false;
        var root = true;
        for (var level = term; level is not null; level = level.LastArgument, root = false)
        {
            if (level.Kind == PortTermKind.Parameter)
            {
                if (!declared.Contains(level.Name))
                    return new(where, $"names the type parameter '${level.Name}', which the block does not declare");
                if (insideExpression)
                    return new(where, $"has an Expression whose type names the parameter '${level.Name}'; its type must be closed");
                if (!isOutput && bodyBound.Contains(level.Name))
                {
                    return new(where, position switch
                    {
                        Position.ScopeVariable => $"names '${level.Name}', which a sub-canvas output binds; only an output may name it",
                        _ => $"is an input naming '${level.Name}', which a sub-canvas output binds; only an output may name it",
                    });
                }

                continue;
            }

            if (level.Kind is not (PortTermKind.Atom or PortTermKind.Constructor))
                continue;
            if (PortTypeWideningTable.Builtin.TryGetAliasCanonical(level.Name, out var aliasCanonical) &&
                !string.Equals(level.Name, aliasCanonical, StringComparison.OrdinalIgnoreCase))
                return new(where, $"spells '{level.Name}' as a library alias; the canonical spelling is '{aliasCanonical}'");
            if (LibraryNames.FirstOrDefault(canonical => IsOtherCase(level.Name, canonical)) is { } spelled)
                return new(where, $"spells '{level.Name}' in a non-canonical case; the canonical spelling is '{spelled}'");
            if (level.Name == "Expression")
            {
                if (!(root && position == Position.Input && level.Kind == PortTermKind.Constructor && !level.IsNullable))
                    return new(where,
                        "has Expression in its type; Expression<T> is allowed only as the whole, non-nullable type of an input");
                insideExpression = true;
            }
        }

        if (PortTermGrading.JsonMisplaced(term, isOutput ? TermPosition.Output : TermPosition.Input))
            return new(where, "has Json in its type; Json is allowed only as the whole type of an output");

        return null;
    }

    private static readonly string[] LibraryNames = ["Json", "Expression", "Collection", "String", "Number", "Int", "Boolean", "DateTime"];

    private static bool IsOtherCase(string name, string canonical) =>
        name != canonical && string.Equals(name, canonical, StringComparison.OrdinalIgnoreCase);

    internal static ArgumentException Invalid(string blockTypeId, string subject, string reason) =>
        new($"{CompilationRules.BlockPortDeclarationInvalid}: Block '{blockTypeId}' {subject} {reason}.", "factory");
}
