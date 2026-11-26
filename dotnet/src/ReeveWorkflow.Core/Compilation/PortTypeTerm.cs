using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Compilation;

internal readonly struct PortTypeTerm : IEquatable<PortTypeTerm>
{
    public int Depth { get; }
    public string Element { get; }

    public PortTypeTerm(int depth, string element)
    {
        Depth = depth;
        Element = element;
    }

    public bool HasPlainElement => PortTypeGrammar.IsAtom(Element);

    public static PortTypeTerm From(PortValueType type)
    {
        var name = type.Name;
        var start = 0;
        var end = name.Length;
        var depth = 0;
        while (end - start > PortTypeConstants.BracketPrefix.Length && name[end - 1] != '?'
            && name.AsSpan(start, PortTypeConstants.BracketPrefix.Length).SequenceEqual(PortTypeConstants.BracketPrefix))
        {
            depth++;
            start += PortTypeConstants.BracketPrefix.Length;
            end--;
        }
        return new PortTypeTerm(depth, name[start..end]);
    }

    public PortTypeTerm Peel() => new(Depth - 1, Element);

    public PortValueType Render()
    {
        var text = new System.Text.StringBuilder(Depth * (PortTypeConstants.BracketPrefix.Length + 1) + Element.Length)
            .Insert(0, PortTypeConstants.BracketPrefix, Depth).Append(Element).Append('>', Depth).ToString();
        return PortValueType.Of(text);
    }

    public bool Equals(PortTypeTerm other) =>
        Depth == other.Depth && string.Equals(Element, other.Element, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is PortTypeTerm other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Depth, StringComparer.OrdinalIgnoreCase.GetHashCode(Element));
}

internal static class PortTypeConstants
{
    public const string BracketPrefix = "Collection<";
}
