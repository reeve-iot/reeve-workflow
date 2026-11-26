namespace ReeveWorkflow.Core.Model;

/// <summary>What a host may keep on a workflow row in <see cref="Workflow.HostMetadata"/>: a few short text entries the
/// library stores and returns and never reads.</summary>
public static class WorkflowHostMetadata
{
    /// <summary>The most entries a workflow row holds.</summary>
    public const int MaxEntries = 16;

    /// <summary>The longest key. Lengths count UTF-16 code units, as <see cref="string.Length"/> does.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>The longest value.</summary>
    public const int MaxValueLength = 256;

    /// <summary>Why <paramref name="metadata"/> is refused, naming the entry, or null. Absent and empty are not refused.</summary>
    public static string? Refusal(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
            return null;

        if (metadata.Count > MaxEntries)
            return $"Host metadata holds {metadata.Count} entries; at most {MaxEntries} are stored.";

        foreach (var key in metadata.Keys.Order(StringComparer.Ordinal))
        {
            if (!IsKey(key))
                return $"Host metadata key '{key}' is refused: a key is 1 to {MaxKeyLength} characters of letters, digits, '_', '.' and '-', starting with a letter.";

            var value = metadata[key];
            if (value is null)
                return $"Host metadata entry '{key}' has no value.";
            if (value.Length > MaxValueLength)
                return $"Host metadata entry '{key}' is refused: a value is at most {MaxValueLength} characters.";
            if (value.Any(char.IsControl))
                return $"Host metadata entry '{key}' is refused: a value holds no control character.";
        }

        return null;
    }

    /// <summary>Throws <see cref="ArgumentException"/> with the refusal when there is one. The check behind the
    /// <see cref="Workflow"/> constructor and <see cref="Workflow.Update"/>, so a host building the entity is held to it too.</summary>
    public static void Require(IReadOnlyDictionary<string, string>? metadata)
    {
        if (Refusal(metadata) is { } refusal)
            throw new ArgumentException(refusal, nameof(metadata));
    }

    /// <summary>The map as it is stored: a copy of it, or null for none or an empty map.</summary>
    public static IReadOnlyDictionary<string, string>? Normalize(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is null or { Count: 0 } ? null : new Dictionary<string, string>(metadata, StringComparer.Ordinal);

    private static bool IsKey(string key) =>
        key.Length is >= 1 and <= MaxKeyLength
        && char.IsAsciiLetter(key[0])
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
}
