using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Editor;

/// <summary>A new workflow and its first version, which is published on creation.</summary>
/// <remarks><paramref name="ImportWarningsJson"/> is here for an import: a graph whose references a host could
/// not resolve carries what it could not find. A member this record does not have
/// is refused (400), not dropped. <c>HostMetadata</c> over its limits is
/// refused as <c>host-metadata-invalid</c>. <c>Origin</c> is <c>Edited</c> when absent; <c>Imported</c>, for an in-process
/// caller, leaves out each secret entry an edit would refuse and lists them in <see cref="WorkflowEditResult.DroppedSecrets"/>. The HTTP create
/// refuses it, <c>origin-not-accepted</c>, because that result is not an HTTP body.</remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateWorkflowRequest(
    string Name,
    string? Description,
    string? DefinitionJson,
    string? InputParametersJson = null,
    string? OutputParametersJson = null,
    IReadOnlyDictionary<string, string>? HostMetadata = null,
    int? ExecutionTimeoutSeconds = null,
    string? RetryPolicyJson = null,
    string? Mode = null,
    int? MaxConcurrentRuns = null,
    int? MaxQueuedRuns = null,
    string? OverflowBehavior = null,
    string? ImportWarningsJson = null,
    string? CheckpointPolicyJson = null,
    [property: JsonConverter(typeof(ExactNameEnumConverter<WorkflowDefinitionOrigin>))] WorkflowDefinitionOrigin? Origin = null);

/// <summary>A change to a workflow. <paramref name="AutoPublish"/> false leaves the change as a draft
/// version, which is what the editor saves while a graph is being worked on. A member this record does not have
/// is refused (400).</summary>
/// <remarks><c>HostMetadata</c> is the whole map the row holds after the save: it replaces the stored one, and null or empty
/// clears it. The limits are those of <see cref="CreateWorkflowRequest"/>.</remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateWorkflowRequest(
    string Name,
    string? Description,
    string? DefinitionJson,
    string? InputParametersJson = null,
    string? OutputParametersJson = null,
    IReadOnlyDictionary<string, string>? HostMetadata = null,
    int? ExecutionTimeoutSeconds = null,
    string? RetryPolicyJson = null,
    string? ChangeMessage = null,
    bool AutoPublish = true,
    string? Mode = null,
    int? MaxConcurrentRuns = null,
    int? MaxQueuedRuns = null,
    string? OverflowBehavior = null,
    string? CheckpointPolicyJson = null)
{
    /// <summary>The <see cref="ReeveWorkflow.Abstractions.WorkflowResponse.Revision"/> the caller's copy was read at.
    /// Every update moves the revision by one in its first write. Set, that write moves it only while it still is this
    /// one, and otherwise the update is refused with <see cref="WorkflowRevisionMismatchException"/> and writes
    /// nothing. Null skips the check: the update moves the revision from whatever it is.</summary>
    public int? ExpectedRevision { get; init; }
}

/// <summary>A document to analyse without saving it: the fields of <see cref="UpdateWorkflowRequest"/> that the compile reads.
/// A member this record does not have is refused (400).</summary>
/// <param name="WorkflowId">The workflow the draft belongs to, which the caller must own; null for one not created yet.</param>
/// <param name="Revision">A number the caller chose for this draft, answered back unchanged so it can tell whether a response
/// is for the draft it holds now.</param>
/// <param name="DefinitionJson">The definition text. Text the stored document reader does not read is answered with the
/// diagnostic <c>definition-unreadable</c>, not refused.</param>
/// <param name="InputParametersJson">The stored input parameter list; null is none.</param>
/// <param name="OutputParametersJson">The stored output parameter list; null is none.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftAnalysisRequest(
    Guid? WorkflowId,
    long Revision,
    string? DefinitionJson,
    string? InputParametersJson = null,
    string? OutputParametersJson = null);

/// <summary>What <c>POST {root}/analysis</c> answers: the diagnostics a save of the draft would report and the ports of every node
/// the binding pass typed, with the types they end with.</summary>
/// <param name="Revision">The request's revision.</param>
/// <param name="RegistryGeneration">The generation of the port-type vocabulary the draft was compiled under. A caller holding
/// another one refetches <c>GET {root}/port-types</c>; generations restart with a process and differ between replicas, so they
/// are compared for equality, never ordered.</param>
/// <param name="Diagnostics">The compile's diagnostics, the shape <c>GET {root}/{versionId}/compilation</c> answers.</param>
/// <param name="Nodes">See <see cref="DraftAnalysis.Nodes"/>.</param>
public sealed record DraftAnalysisResponse(
    long Revision, long RegistryGeneration, IReadOnlyList<CompilationDiagnostic> Diagnostics, IReadOnlyList<NodeAnalysis> Nodes);

/// <summary>What a write did: the workflow as it now reads, and the version it touched when it made one.</summary>
/// <remarks>The version fields are here so a host can write its own audit row without reading the workflow
/// back — the one thing an editor's caller always wants and the response shape does not carry.
/// <para><see cref="VersionPublished"/> false means one of five things: the write left no draft to publish (a save equal
/// to the current version's text; over a draft, it discards the draft), the request did not ask to publish, the version
/// does not compile (<see cref="Diagnostics"/>), the door failed after it committed the save and before any promote
/// committed (<see cref="Fault"/>), or the write ran inside a transaction the host commits (<see cref="PublishDeferred"/>).
/// True means the promote committed.</para></remarks>
public sealed record WorkflowEditResult(
    WorkflowResponse Workflow,
    Guid? VersionId,
    int? VersionNumber,
    bool VersionPublished)
{
    /// <summary>The compiler's diagnostics for the text this write saved, or for the draft a default save repeated, as
    /// read back after the commit. Empty when that text compiles, when the write touched no version, or when that text
    /// was not compiled: a fault (<see cref="Fault"/>), a deferral (<see cref="PublishDeferred"/>), a draft save inside a
    /// transaction the host commits, or a draft another write changed before the read-back.</summary>
    public IReadOnlyList<CompilationDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>What failed after a commit. With <see cref="VersionPublished"/> false: reading the save back, compiling it (a
    /// <see cref="WorkflowCompilationUnansweredException"/>, with a resolver's or the workflow store's fault or a
    /// <see cref="CompilationFaultException"/> inside, or a cancellation) or promoting it; the save stands as the draft,
    /// nothing was published, repeating the request saves again, and a repeated create makes a second workflow. With it
    /// true: the promote committed and only reading the workflow back failed; <see cref="Workflow"/> is then what the
    /// door read with its own promote applied. Null otherwise.</summary>
    public Exception? Fault { get; init; }

    /// <summary>True when the write asked to publish inside a transaction the host commits
    /// (<see cref="IWorkflowEditorTransaction.JoinsAnotherTransaction"/>): the version is saved as the draft, and nothing
    /// was compiled or promoted. After the host commits, <see cref="IWorkflowEditor.PublishAsync"/> publishes it.</summary>
    public bool PublishDeferred { get; init; }

    /// <summary>The references the committed save bound.</summary>
    public IReadOnlyList<string> BoundReferences { get; init; } = [];

    /// <summary>The references the committed save dropped.</summary>
    public IReadOnlyList<string> DroppedReferences { get; init; } = [];

    /// <summary>The secret entries a create with origin <see cref="WorkflowDefinitionOrigin.Imported"/> left out of the stored version.</summary>
    public IReadOnlyList<WorkflowDroppedSecret> DroppedSecrets { get; init; } = [];
}

/// <summary>Whether one version compiles under the catalogue this host has now.</summary>
/// <param name="WorkflowId">The workflow the version belongs to; in a listing, the first workflow the page gave it for.</param>
/// <param name="WorkflowVersionId">The version.</param>
/// <param name="IsValid">True when it compiles.</param>
/// <param name="Diagnostics">Why it does not, in the compiler's order; empty when it compiles.</param>
public sealed record WorkflowCompilationResponse(
    Guid WorkflowId, Guid WorkflowVersionId, bool IsValid, IReadOnlyList<CompilationDiagnostic> Diagnostics);

/// <summary>A version a listing names, by its workflow and its id.</summary>
/// <param name="WorkflowId">The first workflow the page gave the version for.</param>
/// <param name="WorkflowVersionId">The version.</param>
public sealed record CompilationVersionResponse(Guid WorkflowId, Guid WorkflowVersionId);

/// <summary>One page of the caller's workflows that have a current version, and which of those versions do not
/// compile.</summary>
/// <remarks>A page is taken in workflow id order. An empty <paramref name="Invalid"/> says every version on the page
/// compiles only when <paramref name="Failed"/> is empty too. Pages are separate reads: a workflow whose current
/// version moves between two of them is judged as the page that listed it read it.</remarks>
/// <param name="Invalid">The versions on this page that do not compile, with their diagnostics.</param>
/// <param name="Failed">The versions for which whether they compile could not be answered; asking again may answer them.</param>
/// <param name="Unsaved">The versions answered whose verdict the verdict store did not save.</param>
/// <param name="Shared">Each version the page gave for more than one workflow. It is answered once.</param>
/// <param name="TotalCount">How many of the caller's workflows have a current version, across every page.</param>
/// <param name="Skip">The offset this page started at, after clamping.</param>
/// <param name="Take">The page size, after clamping to between 1 and 50.</param>
public sealed record CompilationReportResponse(
    IReadOnlyList<WorkflowCompilationResponse> Invalid,
    IReadOnlyList<CompilationVersionResponse> Failed,
    IReadOnlyList<CompilationVersionResponse> Unsaved,
    IReadOnlyList<CompilationSharedVersion> Shared,
    int TotalCount,
    int Skip,
    int Take);

/// <summary>The 503 that create, and an update that asked to publish, answer with when the save committed and
/// publishing it could not be finished: the compile could not answer, or reading the save back or promoting it
/// failed. The save stands as the draft. Publish it with <c>POST {root}/{id}/publish</c> after <c>Retry-After</c>;
/// sending the create again makes a second workflow.</summary>
/// <param name="Type">The problem type URI ASP.NET gives a 503.</param>
/// <param name="Title">"Service Unavailable".</param>
/// <param name="Status">503.</param>
/// <param name="Detail">What was not done, in the library's words; never the fault's own message.</param>
/// <param name="Workflow">The workflow as the save left it, with the draft.</param>
/// <param name="VersionId">The draft the save wrote or repeated.</param>
/// <param name="VersionNumber">That draft's number.</param>
public sealed record WorkflowSaveUnpublishedProblemDetails(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("workflow")] WorkflowResponse Workflow,
    [property: JsonPropertyName("versionId")] Guid VersionId,
    [property: JsonPropertyName("versionNumber")] int VersionNumber);

/// <summary>The port-type vocabulary the compiler enforces on this host: the library's built-ins and every type and
/// constructor the host and its plugins registered, each with its owner. A host seeds its editor's registry from it
/// rather than restating the vocabulary in TypeScript.</summary>
/// <remarks>The vocabulary is the host's, not a user's: it is the same for every caller. No member is nullable.</remarks>
/// <param name="Generation">The registry generation this was read at. An editor holding another one refetches; generations are compared for equality.</param>
/// <param name="Capabilities">The library's capability vocabulary, closed, in <see cref="PortCapabilities.All"/> order.</param>
/// <param name="Types">Every atom, canonical spelling, in <see cref="PortTypeWideningTable.KnownTypes"/> order.</param>
/// <param name="Constructors">Every constructor, <c>Collection</c> included, sorted by name.</param>
/// <param name="SecretCodec">True when the host registered an <c>IWorkflowSecretCodec</c>, so a <c>protect:</c> value can be stored; an
/// editor offers its hide toggle only then.</param>
public sealed record PortTypeVocabularyResponse(
    long Generation, IReadOnlyList<PortCapabilityDescriptor> Capabilities, IReadOnlyList<PortTypeDescriptor> Types,
    IReadOnlyList<PortTypeConstructorDescriptor> Constructors, bool SecretCodec)
{
    /// <summary>The vocabulary of <paramref name="vocabulary"/>, as <c>GET {root}/port-types</c> answers it from the host's
    /// <see cref="CompilationOptions.Vocabulary"/>: for a host that serves it from a route of its own.</summary>
    /// <param name="vocabulary">The host's vocabulary.</param>
    /// <param name="services">The host's service provider, which says whether it registered an <c>IWorkflowSecretCodec</c>; the codec is not built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="vocabulary"/> or <paramref name="services"/> is null.</exception>
    public static PortTypeVocabularyResponse From(WorkflowTypeVocabulary vocabulary, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(services);
        var secretCodec = services.GetService<IServiceProviderIsService>() is { } registrations
            ? registrations.IsService(typeof(IWorkflowSecretCodec))
            : services.GetService<IWorkflowSecretCodec>() is not null;
        return new(
            vocabulary.Generation,
            [.. PortCapabilities.All.Select(c => new PortCapabilityDescriptor(
                PortCapabilities.NameOf(c), PortCapabilities.AssociatedTypes(c) is { Count: > 0 } associated ? associated : null))],
            [.. vocabulary.Types.Select(type => new PortTypeDescriptor(
                type.Name, type.Owner, type.Aliases, type.WidensTo, [.. type.Implements.Select(PortCapabilities.NameOf)],
                PortLiteralDescriptor.Of(type.Literal)))],
            [.. vocabulary.Constructors.Select(c => new PortTypeConstructorDescriptor(
                c.Name, c.Owner, c.Variance == WorkflowTypeVariance.Covariant ? "covariant" : "invariant", c.AllowedArguments,
                [.. c.Implements.Select(PortCapabilities.NameOf)], PortLiteralDescriptor.Of(c.Literal), c.Arity, c.Key))],
            secretCodec);
    }
}

/// <summary>One capability of <see cref="PortTypeVocabularyResponse"/>.</summary>
/// <param name="Name">The capability's name, as a declaration constrains a type parameter by it and a type's
/// <c>Implements</c> lists it.</param>
/// <param name="Associated">The associated types it names (<c>Item</c> for <c>Iterable</c>); null when it names none.</param>
public sealed record PortCapabilityDescriptor(string Name, IReadOnlyList<string>? Associated);

/// <summary>One atom of <see cref="PortTypeVocabularyResponse"/>.</summary>
/// <param name="Name">The canonical spelling, a plain name or a <c>Collection&lt;…&gt;</c> of one.</param>
/// <param name="Owner"><c>library</c>, <c>host</c> or <c>plugin:&lt;id&gt;</c>.</param>
/// <param name="Aliases">The aliases registered for it; empty when none. Case variants read as it too, unlisted.</param>
/// <param name="WidensTo">Every term it may feed directly, one way: closed, non-nullable document terms; empty when none.
/// A collection widens when its element does, which this list does not repeat.</param>
/// <param name="Implements">The capabilities it implements, by name, in vocabulary order: the library's fixed set for a
/// library atom, the verified claims of the record for a registered one. Empty when none.</param>
/// <param name="Literal">How a literal of it is graded; null when its record declares no form, and every literal of it is
/// refused.</param>
public sealed record PortTypeDescriptor(
    string Name, string Owner, IReadOnlyList<string> Aliases, IReadOnlyList<string> WidensTo, IReadOnlyList<string> Implements,
    PortLiteralDescriptor? Literal = null);

/// <summary>How a literal of a type is graded, as its record declares it.</summary>
/// <param name="GradedAs">The JSON shape: <c>String</c>, <c>Boolean</c>, <c>Int</c>, <c>Number</c> or <c>Json</c>.</param>
/// <param name="Format">The reading of a string literal, <c>Uuid</c> or <c>DateTime</c>; null for none, and always null
/// unless <c>GradedAs</c> is <c>String</c>.</param>
public sealed record PortLiteralDescriptor(string GradedAs, string? Format)
{
    /// <summary>The descriptor of <paramref name="form"/>; null for a type with no form.</summary>
    public static PortLiteralDescriptor? Of(WorkflowLiteralForm? form) =>
        form is null ? null : new(form.GradedAs.ToString(), form.Format?.ToString());
}

/// <summary>One constructor of <see cref="PortTypeVocabularyResponse"/>.</summary>
/// <param name="Name">The constructor's name: <c>Collection</c>, <c>Map</c> or a host's.</param>
/// <param name="Owner"><c>library</c>, <c>host</c> or <c>plugin:&lt;id&gt;</c>.</param>
/// <param name="Variance"><c>covariant</c> for <c>Collection</c> and <c>Map</c> (a map in its value), <c>invariant</c> for every other.</param>
/// <param name="AllowedArguments">The closed terms it admits as its argument, nullable or not; null for
/// <c>Collection</c> and <c>Map</c>, which admit any.</param>
/// <param name="Implements">The capabilities every term over it implements, by name: a registered constructor's verified
/// claims; for <c>Collection</c> only <c>Iterable</c>; for <c>Map</c> none, because a collection or a map is Equatable and
/// JsonSerializable when its argument or value is.</param>
/// <param name="Literal">How a literal of any term over it is graded; null when none is declared, and always for
/// <c>Collection</c>, whose literal is an array of literals of its argument, and for <c>Map</c>, whose literal is an object.</param>
/// <param name="Arity">How many arguments a term over it takes: 1, and 2 for <c>Map</c>.</param>
/// <param name="Key">The first argument of a constructor whose key is fixed: <c>String</c> for <c>Map</c>; null for every other.</param>
public sealed record PortTypeConstructorDescriptor(
    string Name, string Owner, string Variance, IReadOnlyList<string>? AllowedArguments, IReadOnlyList<string> Implements,
    PortLiteralDescriptor? Literal = null, int Arity = 1, string? Key = null);
