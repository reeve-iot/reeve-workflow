using System.Security.Cryptography;
using System.Text;

namespace ReeveWorkflow.Core.Execution;

/// <summary>What every implementation of <see cref="IWorkflowFireDedup"/> needs and none of them should write
/// twice: the one-way token a fire key is allowed to appear as in a log.</summary>
public static class WorkflowFireKey
{
    /// <summary>A non-reversible token for logging a fire key. The keys derive from things like a user id and an
    /// exact position, so the raw key must never reach the logs; this may, and correlates two log lines about
    /// one fire.</summary>
    /// <param name="fireKey">The raw key. Null or empty answers <c>"-"</c>, because a caller that could not
    /// derive a key still has to log the fire it let through.</param>
    /// <remarks>Upper-case hex, which is the form the library's first adopter already writes. A grep is
    /// case-sensitive, and the window in which two replicas run different implementations is exactly the
    /// window in which someone is correlating their two log lines about one fire.</remarks>
    public static string Fingerprint(string? fireKey) =>
        string.IsNullOrEmpty(fireKey)
            ? "-"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fireKey)))[..16];
}
