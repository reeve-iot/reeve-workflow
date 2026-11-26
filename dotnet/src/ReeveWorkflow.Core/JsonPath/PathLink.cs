using System.Globalization;
using System.Text;

namespace ReeveWorkflow.Core.JsonPath;

internal sealed class PathLink
{
    private readonly PathLink? _parent;
    private readonly string? _name;
    private readonly int _index;

    public PathLink(PathLink? parent, string name)
    {
        _parent = parent;
        _name = name;
    }

    public PathLink(PathLink? parent, int index)
    {
        _parent = parent;
        _index = index;
    }

    public static string Format(PathLink? link)
    {
        if (link is null) return "$";
        var steps = new List<PathLink>();
        for (var step = link; step is not null; step = step._parent) steps.Add(step);
        var builder = new StringBuilder("$");
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            var step = steps[i];
            if (step._name is null)
            {
                builder.Append('[').Append(step._index.ToString(CultureInfo.InvariantCulture)).Append(']');
                continue;
            }
            builder.Append("['");
            AppendEscaped(builder, step._name);
            builder.Append("']");
        }
        return builder.ToString();
    }

    private static void AppendEscaped(StringBuilder builder, string name)
    {
        foreach (var c in name)
        {
            switch (c)
            {
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\'': builder.Append("\\'"); break;
                case '\\': builder.Append("\\\\"); break;
                default:
                    if (c < 0x20)
                        builder.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }
    }
}
