namespace ReeveWorkflow.Core.Hosting;

/// <summary>The one rule for a name that probably holds a secret. Trace masking, the save path and a host's own masking all ask it.</summary>
public static class WorkflowSensitiveNames
{
    private static readonly string[] Fragments =
    [
        "password", "token", "secret", "apikey", "credential", "authorization", "privatekey", "cookie",
    ];

    /// <summary>True when <paramref name="name"/> contains, ignoring case and the separators <c>-</c>, <c>_</c> and space,
    /// <c>password</c>, <c>token</c>, <c>secret</c>, <c>apikey</c>, <c>credential</c>, <c>authorization</c>, <c>privatekey</c> or
    /// <c>cookie</c>. It does not match <c>Ocp-Apim-Subscription-Key</c>, <c>X-Hub-Signature</c>, <c>X-Auth</c> or a bare <c>Key</c>.</summary>
    public static bool Matches(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var folded = name.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        foreach (var fragment in Fragments)
        {
            if (folded.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
