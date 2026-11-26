namespace ReeveWorkflow.Core.Execution;

internal static class RunTimeValueReading
{
    public static bool TryInt(object? raw, out long value)
    {
        switch (raw)
        {
            case long integer:
                value = integer;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
