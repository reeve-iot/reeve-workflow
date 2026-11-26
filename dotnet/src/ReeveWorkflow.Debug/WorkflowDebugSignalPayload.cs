using System.Text.Json;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Debug;

/// <summary>How the debugger's two payloads are written on the one cluster-signal transport, which carries an
/// id and a string.</summary>
/// <remarks>Public because a host adapting the transport to a bus of its own has to read what the library
/// wrote, and write what the library will read. Both encodings are wire formats: a mixed-version cluster has
/// one replica writing them and another parsing them.</remarks>
public static class WorkflowDebugSignalPayload
{
    /// <summary>Writes a command as its name, so a raw channel reads as English and the wire stops depending on
    /// numbers that are also persisted in rows — two reasons to change one, and neither of them the wire's.</summary>
    public static string Write(WorkflowDebugCommand command) => command.ToString();

    /// <summary>Reads a command back. False for anything else, including a name from a newer version — a
    /// command this replica cannot perform is dropped rather than guessed at.</summary>
    /// <remarks>The written name has to come back, which is what refuses a number: <c>Enum.TryParse</c>
    /// accepts one, and a number is the encoding this pair exists to keep off the wire.</remarks>
    public static bool TryReadCommand(string? payload, out WorkflowDebugCommand command) =>
        Enum.TryParse(payload, ignoreCase: false, out command)
        && Enum.IsDefined(command)
        && string.Equals(Write(command), payload, StringComparison.Ordinal);

    /// <summary>Writes a breakpoint set as a JSON array. Node ids are arbitrary strings, so a separator
    /// character would be a bug waiting for the first id that contains it.</summary>
    public static string Write(IReadOnlyList<string> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        return JsonSerializer.Serialize(nodeIds);
    }

    /// <summary>Reads a breakpoint set back. False for anything that is not a JSON array of non-null strings —
    /// a null element would reach the run as a node id nothing matches; an empty array is a legitimate set and
    /// reads as true.</summary>
    public static bool TryReadNodeIds(string? payload, out IReadOnlyList<string> nodeIds)
    {
        nodeIds = [];
        if (string.IsNullOrEmpty(payload))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<string?[]>(payload);
            if (parsed is null || Array.Exists(parsed, id => id is null))
                return false;

            nodeIds = parsed!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
