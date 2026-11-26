namespace ReeveWorkflow.Core.Tests.TestSupport;

internal static class ExpressionGrid
{
    private static readonly string[] Operands = [
        "${previous.n.i}", "${previous.n.x}", "${previous.n.s}", "${previous.n.b}", "${previous.n.d}", "${previous.n.j}",
        "${previous.n.ni}", "${previous.n.ki}", "${previous.n.nx}", "${previous.n.kx}", "${previous.n.ns}", "${previous.n.ks}",
        "${previous.n.nb}", "${previous.n.ci}", "${previous.n.cn}", "${previous.n.cni}", "${previous.n.cs}", "${previous.n.big}",
        "${previous.n.lo}", "${previous.n.zero}", "${previous.n.zx}", "${previous.n.huge}", "${parameter.p}", "${parameter.q}",
        "${previous.n.jn}", "${trig.payload}", "${trig.label}", "${trig.flag}", "${now}", "${now.hour}", "${helper.level}",
        "1", "0", "2.5", "\"7\"", "\"\"", "true", "false", "null",
    ];
    private static readonly string[] Binary = ["+", "-", "*", "/", "%", "<", "<=", ">", ">=", "==", "!=", "&&", "||"];
    private static readonly string[] Unary = ["-", "!"];
    private static readonly string[] One = ["abs", "round", "floor", "ceil", "len", "toNumber", "toString", "min", "max"];
    private static readonly string[] Two = ["min", "max", "contains", "startsWith", "endsWith"];
    private static readonly string[] Conditions = ["${previous.n.b}", "${trig.flag}", "${previous.n.i} > 1", "${previous.n.nb}", "1"];
    private static readonly string[] Deep = ["${previous.n.i}", "${previous.n.x}", "${previous.n.s}", "${previous.n.ki}", "${previous.n.kx}", "${previous.n.ks}", "${previous.n.ci}", "${previous.n.cni}", "${previous.n.big}", "${previous.n.huge}", "2.5", "null"];
    private static readonly string[] DeepOperators = ["+", "-", "*", "<", "=="];

    public static IEnumerable<string> Grid()
    {
        foreach (var a in Operands)
        {
            foreach (var op in Unary) yield return $"{op}{a}";
            foreach (var fn in One) yield return $"{fn}({a})";
            foreach (var b in Operands)
            {
                foreach (var op in Binary) yield return $"{a} {op} {b}";
                foreach (var fn in Two) yield return $"{fn}({a}, {b})";
                foreach (var condition in Conditions) yield return $"{condition} ? {a} : {b}";
            }
        }
        yield return "min(${previous.n.i}, ${previous.n.x}, 3)";
        yield return "max(1, 2, 3.5, ${previous.n.zero})";
        foreach (var nested in Nested()) yield return nested;
    }

    public static IEnumerable<string> Nested()
    {
        foreach (var a in Deep)
        foreach (var b in Deep)
        foreach (var inner in DeepOperators)
        {
            foreach (var c in Deep)
            {
                foreach (var outer in DeepOperators) yield return $"({a} {inner} {b}) {outer} {c}";
                yield return $"${{previous.n.b}} ? ({a} {inner} {b}) : {c}";
            }
            yield return $"abs({a} {inner} {b})";
            yield return $"len(toString({a} {inner} {b}))";
            yield return $"min(round({a}), {b})";
            yield return $"toString({a} {inner} {b}) + \"!\"";
        }
    }
}
