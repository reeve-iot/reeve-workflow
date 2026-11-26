using System.Text.Json.Serialization;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>What a value of a secret-capable input (<see cref="Abstractions.BlockPortDefinition.SecretValues"/>) is, as the host's
/// codec classifies it.</summary>
public enum WorkflowSecretForm
{
    /// <summary>Text the library may show and store as written.</summary>
    Plain,

    /// <summary>Ciphertext the codec produced for one workflow.</summary>
    Protected,

    /// <summary>A pointer to a secret the host holds elsewhere; the pointer itself is not secret.</summary>
    Reference,
}

/// <summary>Where an entry of a secret-capable input lives: the workflow, the canvas, the node, the input and the key. A node id is
/// unique per canvas only, so the canvas path is part of the site.</summary>
/// <param name="WorkflowId">The workflow whose version holds the entry; a sub-workflow is its own workflow.</param>
/// <param name="CanvasPath">The canvas as <see cref="CanvasPathText"/> writes it; empty at the root.</param>
/// <param name="NodeId">The node.</param>
/// <param name="Input">The input.</param>
/// <param name="Key">The entry's key.</param>
public sealed record WorkflowSecretSite(Guid WorkflowId, string CanvasPath, string NodeId, string Input, string Key)
{
    /// <summary>The text a site carries for <paramref name="path"/>: each segment's node id and branch name percent-encoded and joined
    /// by <c>/</c>, so two different canvases never write alike. Empty at the root. Opaque: compare it, never parse it.</summary>
    public static string CanvasPathText(CanvasPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return string.Join('/', path.Segments.Select(s => $"{Uri.EscapeDataString(s.NodeId)}/{Uri.EscapeDataString(s.BranchName)}"));
    }
}

/// <summary>Where a definition being saved came from. It travels by its exact member name.</summary>
[JsonConverter(typeof(ExactNameEnumConverter<WorkflowDefinitionOrigin>))]
public enum WorkflowDefinitionOrigin
{
    /// <summary>A user edited it in this workflow; an entry that cannot be kept is refused.</summary>
    Edited,

    /// <summary>It was imported from elsewhere; an entry that cannot be kept is dropped and reported.</summary>
    Imported,
}

/// <summary>The save a codec is asked to admit references for.</summary>
/// <param name="UserId">The user whose save it is; a background save with no HTTP principal is judged like any other.</param>
/// <param name="WorkflowId">The target workflow, allocated before any entry is decided.</param>
/// <param name="Origin">Where the definition came from.</param>
public sealed record WorkflowSecretSave(Guid UserId, Guid WorkflowId, WorkflowDefinitionOrigin Origin);

/// <summary>The host's answer to what a secret is. Optional and scoped: with none registered, values are used as written and
/// <c>protect:</c> is refused. The library knows no prefix, no cipher and no provider.</summary>
/// <remarks>No method may put a plaintext, a ciphertext or a reference's resolved value in an exception message: the library
/// shows a failure by input and key only, and discards the message of one it does not know. It logs the exception's type and the entry,
/// never the message, so a codec logs its own cause.</remarks>
public interface IWorkflowSecretCodec
{
    /// <summary>Names the form of <paramref name="value"/>.</summary>
    WorkflowSecretForm Classify(string value);

    /// <summary>Turns <paramref name="plaintext"/> into a protected form bound to its workflow; a host may key the protection by
    /// <see cref="WorkflowSecretSite.WorkflowId"/>. The input is protected as text whatever it looks like: one that already reads
    /// as a protected form is protected again, never returned as given.</summary>
    /// <exception cref="WorkflowSecretRefusedException">The host refuses to protect it.</exception>
    ValueTask<string> ProtectAsync(WorkflowSecretSite site, string plaintext, CancellationToken cancellationToken);

    /// <summary>Checks, and only checks, the references a save binds and the ones it drops, for <see cref="WorkflowSecretSave.UserId"/>.
    /// Not called when both lists are empty.</summary>
    /// <exception cref="WorkflowSecretRefusedException">The host refuses the save.</exception>
    ValueTask AdmitReferencesAsync(
        WorkflowSecretSave save, IReadOnlyList<string> bound, IReadOnlyList<string> unbound, CancellationToken cancellationToken);

    /// <summary>The plaintext of a protected or referenced value stored at <paramref name="site"/>.</summary>
    /// <exception cref="WorkflowSecretUnresolvedException">The value cannot be revealed.</exception>
    ValueTask<string> RevealAsync(WorkflowSecretSite site, string stored, CancellationToken cancellationToken);
}
