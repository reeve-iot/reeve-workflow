using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>What the library does with the secret entries of a definition: what a save stores for them, and what a text it hands out
/// shows. The editor and the catalog resolve it, and a host writer outside the editor calls it.</summary>
/// <remarks>Every member reads the same entries: each string entry of a secret-capable input's literal object, in a node's
/// <c>configuration</c> and in a literal mapping (<c>type</c> <c>literal</c>) of an edge into that node, in every canvas.</remarks>
public interface IWorkflowDefinitionSecrets
{
    /// <summary>Answers <paramref name="definitionJson"/> with each entry of a secret-capable input's literal that the library
    /// does not show replaced by <c>***</c>: one the registered <see cref="IWorkflowSecretCodec"/> classifies
    /// <see cref="WorkflowSecretForm.Protected"/>, and a <see cref="WorkflowSecretForm.Plain"/> one whose key
    /// <see cref="WorkflowSensitiveNames"/> matches. A <see cref="WorkflowSecretForm.Reference"/> is returned as written. Every other byte
    /// of the text is returned as it was; text that holds no such entry, or that the stored-document lexer refuses, is returned unchanged.</summary>
    /// <remarks>A node whose block is not registered is returned as stored, because masking needs the declaration, and so is text the
    /// stored-document reader refuses (a repeated member, an unknown key): it cannot compile or run. A host that exports
    /// definitions itself calls this on the text it exports.</remarks>
    string Mask(string definitionJson);

    /// <summary>Decides every entry of every secret-capable input of <paramref name="definition"/> and answers the definition in stored forms.
    /// <c>***</c> keeps the entry at the same place (workflow, canvas, node, input, configuration or edge, key) of
    /// <paramref name="replacedDefinition"/>, a <c>protect:</c> value and a name-rule plaintext are protected, a protected value must equal
    /// the stored entry, and the references bound and dropped go to one <see cref="IWorkflowSecretCodec.AdmitReferencesAsync"/> call.
    /// A key that is not a field name is refused. Sites are judged under <see cref="WorkflowSecretSave.WorkflowId"/>, the workflow the
    /// definition is saved to.</summary>
    /// <remarks>With <see cref="WorkflowSecretSave.Origin"/> <see cref="WorkflowDefinitionOrigin.Imported"/> nothing is refused for a
    /// secret: each entry an edit would refuse is left out of the answer and listed in <see cref="WorkflowSecretAdmission.DroppedSecrets"/>
    /// with its reason. When the codec refuses the references the save binds, every one of them is left out, and the codec is asked
    /// again without them; a refusal of that second call, or of a call that binds nothing, still refuses the save, because there is
    /// nothing left to drop. A key that is no field name is refused for an import too, and never named: it may itself be a pasted secret.
    /// It reads the text the stored-document reader accepts: a host that writes by its own road runs
    /// <c>WorkflowDefinitionAdmission.EnsureReadable</c> first, as the editor does.</remarks>
    /// <param name="save">The save; a background save with no HTTP principal is judged like any other.</param>
    /// <param name="replacedDefinition">The definition of the version the save replaces (the draft when one exists, else the current
    /// version); null for a create.</param>
    /// <param name="definition">The definition as the caller sent it.</param>
    /// <param name="cancellationToken">Cancels the codec calls.</param>
    /// <exception cref="WorkflowSecretSaveRefusedException">An entry or a reference is refused; nothing is stored.</exception>
    ValueTask<WorkflowSecretAdmission> AdmitAsync(
        WorkflowSecretSave save, string? replacedDefinition, string definition, CancellationToken cancellationToken);

    /// <summary>Admits the references that <paramref name="definition"/>, a copy of an older version of the same workflow, binds and drops
    /// against <paramref name="replacedDefinition"/>, and answers it unchanged. Its entries were stored for this workflow, so none is
    /// decided; the editor calls it for a rollback.</summary>
    /// <param name="save">The save.</param>
    /// <param name="replacedDefinition">The definition of the version the copy replaces (the draft when one exists, else the current version).</param>
    /// <param name="definition">The stored definition being copied.</param>
    /// <param name="cancellationToken">Cancels the codec call.</param>
    /// <exception cref="WorkflowSecretSaveRefusedException">The host refused the references; nothing is stored.</exception>
    /// <exception cref="NotSupportedException"><paramref name="save"/> has origin <see cref="WorkflowDefinitionOrigin.Imported"/>: a restored version is not an import.</exception>
    ValueTask<WorkflowSecretAdmission> AdmitRestoredAsync(
        WorkflowSecretSave save, string? replacedDefinition, string definition, CancellationToken cancellationToken);

    /// <summary>The entries an import of <paramref name="definitionJson"/> as a new workflow would drop for their form, in document
    /// order: each <c>***</c> and each protected value, since a new workflow stores no entry to match them with.</summary>
    /// <remarks>It asks the codec to classify and nothing else: it calls neither <see cref="IWorkflowSecretCodec.ProtectAsync"/> nor
    /// <see cref="IWorkflowSecretCodec.AdmitReferencesAsync"/>, so it does not predict a drop those calls decide (a <c>protect:</c> or a
    /// name-rule value the host refuses to protect, a reference the host refuses) or a <c>protect:</c> form with no codec registered. Text the stored-document reader refuses holds none.</remarks>
    IReadOnlyList<WorkflowDroppedSecret> PredictImportDrops(string definitionJson);
}

/// <summary>What <see cref="IWorkflowDefinitionSecrets.AdmitAsync"/> answers.</summary>
/// <param name="Definition">The definition in stored forms; the text it was given when no entry changed.</param>
/// <param name="BoundReferences">The distinct references the save binds: a reference at a place whose stored entry is not equal to it.</param>
/// <param name="DroppedReferences">The distinct references the save drops: a stored reference whose place no longer holds an equal entry.</param>
public sealed record WorkflowSecretAdmission(
    string Definition, IReadOnlyList<string> BoundReferences, IReadOnlyList<string> DroppedReferences)
{
    /// <summary>The entries an <see cref="WorkflowDefinitionOrigin.Imported"/> save left out of <see cref="Definition"/>, each with its reason; empty for an edit.</summary>
    public IReadOnlyList<WorkflowDroppedSecret> DroppedSecrets { get; init; } = [];
}

/// <summary>A save the secret rules refuse. The message names the site and never a value; the editor answers it as a 400 with <see cref="Code"/>.</summary>
/// <param name="code">A <see cref="WorkflowSaveRefusalCodes"/> value.</param>
/// <param name="message">What is wrong and what to send instead.</param>
public sealed class WorkflowSecretSaveRefusedException(string code, string message) : Exception(message)
{
    /// <summary>A <see cref="WorkflowSaveRefusalCodes"/> value.</summary>
    public string Code { get; } = code;
}
