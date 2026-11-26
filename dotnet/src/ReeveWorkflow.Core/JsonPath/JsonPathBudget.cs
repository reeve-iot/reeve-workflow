using System.Runtime.InteropServices;
using System.Text.Json;

namespace ReeveWorkflow.Core.JsonPath;

internal struct JsonPathBudget
{
    public const int MaxSteps = 100_000;
    public const long MaxSelectedBytes = 16L * 1024 * 1024;

    private int _steps;
    private long _selectedBytes;

    public void Step()
    {
        if (++_steps > MaxSteps) throw new JsonPathBudgetException($"takes more than {MaxSteps} steps");
    }

    public void Steps(long count)
    {
        if (count > MaxSteps - _steps) throw new JsonPathBudgetException($"takes more than {MaxSteps} steps");
        _steps += (int)count;
    }

    public void Select(JsonElement value)
    {
        _selectedBytes += JsonMarshal.GetRawUtf8Value(value).Length;
        if (_selectedBytes > MaxSelectedBytes)
            throw new JsonPathBudgetException($"selects more than {MaxSelectedBytes} bytes of JSON");
    }
}
