using Xunit;

namespace ReeveWorkflow.Debug.Tests;

public sealed class MachineSpeedFactAttribute : FactAttribute
{
    public MachineSpeedFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REEVE_RUN_MACHINE_SPEED_TESTS") != "1")
            Skip = "machine-speed test: set REEVE_RUN_MACHINE_SPEED_TESTS=1 to run it; CI does not.";
    }
}
