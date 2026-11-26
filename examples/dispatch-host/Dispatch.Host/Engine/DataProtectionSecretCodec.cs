using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;

namespace Dispatch.Host.Engine;

internal sealed class DataProtectionSecretCodec(IDataProtectionProvider provider, ILogger<DataProtectionSecretCodec> logger) : IWorkflowSecretCodec
{
    private const string Prefix = "enc:";

    public WorkflowSecretForm Classify(string value) =>
        value.StartsWith(Prefix, StringComparison.Ordinal) ? WorkflowSecretForm.Protected : WorkflowSecretForm.Plain;

    public ValueTask<string> ProtectAsync(WorkflowSecretSite site, string plaintext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Prefix + ProtectorOf(site.WorkflowId).Protect(plaintext));

    public ValueTask AdmitReferencesAsync(
        WorkflowSecretSave save, IReadOnlyList<string> bound, IReadOnlyList<string> unbound, CancellationToken cancellationToken) =>
        bound.Count == 0 ? ValueTask.CompletedTask : throw new WorkflowSecretRefusedException("This desk holds no references.");

    public ValueTask<string> RevealAsync(WorkflowSecretSite site, string stored, CancellationToken cancellationToken)
    {
        if (Classify(stored) != WorkflowSecretForm.Protected)
            throw new WorkflowSecretUnresolvedException();
        try
        {
            return ValueTask.FromResult(ProtectorOf(site.WorkflowId).Unprotect(stored[Prefix.Length..]));
        }
        catch (CryptographicException exception)
        {
            logger.LogWarning("A hidden header could not be revealed: {Cause}", exception.Message);
            throw new WorkflowSecretUnresolvedException();
        }
    }

    private IDataProtector ProtectorOf(Guid workflowId) => provider.CreateProtector("dispatch.workflow-secret", workflowId.ToString("N"));
}
