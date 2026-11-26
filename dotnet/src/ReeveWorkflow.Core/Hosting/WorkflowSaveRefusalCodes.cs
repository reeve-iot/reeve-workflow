namespace ReeveWorkflow.Core.Hosting;

/// <summary>The codes a save is refused with at status 400, before anything is stored: a create, an update, a rollback, or a host
/// writer's <see cref="IWorkflowDefinitionSecrets.AdmitAsync"/>.</summary>
public static class WorkflowSaveRefusalCodes
{
    /// <summary>The host metadata breaks one of the limits of <see cref="Model.WorkflowHostMetadata"/>; the message names the entry.</summary>
    public const string HostMetadataInvalid = "host-metadata-invalid";

    /// <summary>An entry of a secret-capable input is <c>***</c> or a protected value, and the version the save replaces holds no equal entry at that place.</summary>
    public const string SecretValueUnmatched = "secret-value-unmatched";

    /// <summary>An entry is a <c>protect:</c> form, and the host registered no <see cref="IWorkflowSecretCodec"/>.</summary>
    public const string SecretCodecMissing = "secret-codec-missing";

    /// <summary>The host's codec refused to protect an entry or to admit the references the save binds or drops; the message carries its reason.</summary>
    public const string SecretRefused = "secret-refused";

    /// <summary>A key of a secret-capable input's map is not a field name (an RFC 9110 token), as a header written whole as <c>Name: value</c> is; the message names the input and the position, never the key.</summary>
    public const string SecretKeyInvalid = "secret-key-invalid";

    /// <summary>An HTTP create declared an origin other than <c>Edited</c>; an import is made, and its drops reported, only by an in-process caller.</summary>
    public const string OriginNotAccepted = "origin-not-accepted";
}
