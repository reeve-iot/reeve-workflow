namespace ReeveWorkflow.Core.Execution;

internal static class FieldNames
{
    internal static int FirstNonToken(string name)
    {
        if (name.Length == 0)
            return 0;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)))
                return i;
        }

        return -1;
    }

    internal static string Naming(string name, int row) => FirstNonToken(name) switch
    {
        -1 => $"key '{name}'",
        0 => $"row {row}",
        var at => $"key '{name[..at]}...'",
    };
}
