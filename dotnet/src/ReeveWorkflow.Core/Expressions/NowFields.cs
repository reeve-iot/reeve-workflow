namespace ReeveWorkflow.Core.Expressions;

internal static class NowFields
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(
    [
        "year", "month", "day", "hour", "minute", "second", "dayofweek", "iso", "epoch",
        "localyear", "localmonth", "localday", "localhour", "localminute", "localsecond",
        "localdayofweek", "localiso",
    ], StringComparer.OrdinalIgnoreCase);
}
