namespace ReeveWorkflow.Core.Compilation;

/// <summary>Every rule the compiler can refuse under. Kebab-case to match the names the shared validation
/// corpus already grades; a rule that changes its id is a rule a stored verdict can no longer be read by.</summary>
/// <remarks>This is persisted vocabulary, not an implementation detail of <see cref="CompilationDiagnostic"/>:
/// the corpus grades these names and the editor links against them, so every edit here is a compatibility
/// event in a way a change to the record next to it is not.</remarks>
public static class CompilationRules
{
    public const string Cycle = "cycle";
    public const string NoStartNode = "no-start-node";
    public const string InputHasIncoming = "input-has-incoming";
    public const string ExtraRoot = "extra-root";
    public const string Unreachable = "unreachable";
    public const string DuplicateInputMapping = "duplicate-input-mapping";
    public const string DuplicateEdgePair = "duplicate-edge-pair";
    public const string Structure = "structure";

    public const string BlockTypeUnknown = "block-type-unknown";

    /// <summary>A node names a sub-canvas its block type never declared in <c>SubCanvasNames</c>. Distinct
    /// from the empty signature that branch then resolves to, so the wire refusals that follow from it read
    /// as a consequence of this rule rather than as an unrelated mystery.</summary>
    public const string SubCanvasNameUnknown = "sub-canvas-name-unknown";

    /// <summary>A connection names a <c>SourceNodeId</c> or <c>TargetNodeId</c> absent from its own canvas. Every walk
    /// that matters at run time — <c>ValidateStartAndReachability</c>, <c>TopologicalSort</c>, <c>ApplyMappings</c> — skips such
    /// an entry rather than failing on it, so nothing else ever reports it; a wire into the root's exit node is one.</summary>
    public const string EdgeEndpointUnknown = "edge-endpoint-unknown";

    public const string DuplicateNodeId = "duplicate-node-id";
    public const string DuplicateEdgeId = "duplicate-edge-id";
    public const string OutputMappingsUnread = "output-mappings-unread";
    public const string StoredKeyUnknown = "stored-key-unknown";
    /// <summary>A name split by expression path syntax contains a dot.</summary>
    public const string DottedName = "dotted-name";
    public const string RequiredInputUnfed = "required-input-unfed";
    public const string RequiredConfigMissing = "required-config-missing";
    public const string WirePortUnknown = "wire-port-unknown";
    public const string DeclaredOutputShadowsStatic = "declared-output-shadows-static";
    public const string PortTypeMismatch = "port-type-mismatch";
    public const string LiteralPortValueInvalid = "literal-port-value-invalid";

    /// <summary>The text in a stored type field — a dynamic port, a declared output, or a workflow
    /// parameter — is absent, blank, or not a sentence of the port type grammar.
    /// Refused where it is written; every check downstream of that node is skipped rather than stacked on
    /// top of it, so this never appears alongside <see cref="PortTypeMismatch"/>, <see cref="PortTypeUnresolved"/>
    /// or <see cref="WirePortUnknown"/> for the same port.</summary>
    public const string PortTypeMalformed = "port-type-malformed";

    /// <summary>A stored <c>Map</c> term whose key is not the atom <c>String</c>: another name, a nullable <c>String?</c>, a
    /// constructor. Refused where the term is written, as <see cref="PortTypeMalformed"/> is.</summary>
    public const string PortTypeMapKey = "port-type-map-key";

    /// <summary>A stored type is spelled by an alias or a case variant of a registered name (<c>string</c> for
    /// <c>String</c>); a stored alias is refused.</summary>
    public const string PortTypeAlias = "port-type-alias";

    /// <summary>A stored type is well-formed but no name the host's vocabulary registers (its widening table, declared
    /// or widened to): the compile refuses it, while the editor still loads it unchanged.</summary>
    public const string PortTypeUnregistered = "port-type-unregistered";
    /// <summary>A well-formed term holds <c>Json</c> where the port type grammar does not allow it: anywhere in a stored input term (a dynamic input,
    /// an input parameter, a type argument, a projection's type), nested in any stored term or a resolver's or namespace check's
    /// answer, in an input of a resolver's answer, or in a type parameter's binding.</summary>
    public const string PortTypeJsonPosition = "port-type-json-position";

    /// <summary>A projection wire breaks the projection rules: wrong source or mapping, a malformed or restricted path, invalid type placement or delivery,
    /// or an incompatible target (singularity or <c>jsonText</c>).</summary>
    public const string JsonProjectionInvalid = "json-projection-invalid";

    /// <summary>A node supplies an argument key its block does not declare as a type parameter.</summary>
    public const string TypeArgumentUnknown = "type-argument-unknown";

    /// <summary>A node holds a port list under <c>InputPorts</c> or <c>OutputPorts</c> in its configuration, and its block
    /// declares no dynamic ports: the run reads no such list, so it is refused rather than left to do nothing.</summary>
    public const string PortListWithoutDynamicPorts = "port-list-without-dynamic-ports";

    /// <summary>A node declares outputs (<c>outputPorts</c>) and its block supports no declared outputs; the editor offers
    /// none there, and the port would exist in no list a wire could be drawn from.</summary>
    public const string DeclaredOutputsUnsupported = "declared-outputs-unsupported";

    /// <summary>A declared output takes a source from a key that is no sub-canvas of its block; a branch the block does not
    /// declare is never run, so the key projects nothing.</summary>
    public const string DeclaredOutputSourceUnknown = "declared-output-source-unknown";

    /// <summary>The stored definition breaks a rule of the one reader, <c>StoredWorkflowDocument</c> (rules L1 to L7
    /// of docs/reference/validation-findings.md, "Stored-document rules"). One diagnostic names the rule and the JSON location; nothing else is compiled.</summary>
    public const string DefinitionUnreadable = "definition-unreadable";

    /// <summary>An exception escaped a compiler phase: code the compiler called for a node (a namespace check, or a
    /// block shape source a caller passed) threw, or the compiler has a defect. Names the phase, the canvas and, where host code threw at
    /// one, the node. <c>WorkflowCompilationService</c> memoises and stores no verdict for it; it throws
    /// <see cref="CompilationFaultException"/>.</summary>
    public const string CompilationFault = "compilation-fault";

    /// <summary>A generic port's declared relation names another port that resolves to nothing — nothing
    /// wired into the input it follows, or no wire into the branch output it collects. An uninstantiated
    /// type parameter is not a type, so this refuses rather than falling back to <c>Any</c>.</summary>
    public const string PortTypeUnresolved = "port-type-unresolved";
    /// <summary>A block declaration breaks the rules of a declaration, refused when the catalogue is read: a port type
    /// that names an undeclared type parameter, a parameter declared twice or constrained by an unknown capability, a
    /// body-bound parameter on an input, <c>Json</c> anywhere but as the whole type of an output, or an
    /// <c>Expression&lt;T&gt;</c> anywhere but as the whole type of an input over a closed <c>T</c>. Names the block and the
    /// port.</summary>
    public const string BlockPortDeclarationInvalid = "block-port-declaration-invalid";
    /// <summary>A type parameter of a node has two exact bindings that differ, or lower bounds with no least upper bound.
    /// Names the node and the parameter.</summary>
    public const string TypeParameterConflict = "type-parameter-conflict";
    /// <summary>A site needs a type parameter that nothing bound: a literal such as <c>[]</c> or <c>null</c> on a port whose
    /// term names it, or a reader of a port whose term names it. Names the node, the port and the parameter.</summary>
    public const string TypeParameterUnbound = "type-parameter-unbound";
    /// <summary>A type parameter's binding does not implement a constraint the block declares for it. Names the node and the parameter.</summary>
    public const string TypeParameterConstraint = "type-parameter-constraint";
    public const string ExpressionSyntax = "expression-syntax";
    public const string ExpressionOutOfScope = "expression-out-of-scope";
    public const string ExpressionUnknownNamespace = "expression-unknown-namespace";

    /// <summary>A member or an index is read out of a value whose declared type allows none: a String, Number, Boolean
    /// or DateTime, a member of a collection, or a type that states no shape (a host's own type name such as
    /// <c>Device</c>). Only a Json value and one the compiler does not type allow a field; the run
    /// answers it.</summary>
    public const string ExpressionFieldAccessInvalid = "expression-field-access-invalid";

    /// <summary>An operator or function has no overload that accepts the terms of its operands, a <c>?:</c> has no
    /// least upper bound or a non-Boolean condition, or the expression is a Json value, which is no result.</summary>
    public const string ExpressionTypeMismatch = "expression-type-mismatch";

    /// <summary>The result term of an <c>expr:</c> value is not assignable to its target: the effective term of the input
    /// it feeds, the term of the configuration field it fills, or <c>T</c> on an <c>Expression&lt;T&gt;</c> input.</summary>
    public const string ExpressionResultTypeMismatch = "expression-result-type-mismatch";

    /// <summary>A stored parameter list breaks a rule of the one reader: one diagnostic per list, naming the rule
    /// and the JSON location. No list at all, and <c>[]</c>, are no parameters; blank text and the JSON literal <c>null</c> are refused.</summary>
    public const string SignatureUnreadable = "signature-unreadable";

    /// <summary>A node whose block says <c>PortsAreResolvedElsewhere</c> and nobody answered for: no
    /// resolver filed a signature, or the node's reference input was never fed at all, so no answer could
    /// ever be trusted. "I do not know this node's signature" is a refusal, not a reason to let it through.</summary>
    public const string NodePortsUnresolved = "node-ports-unresolved";

    /// <summary>A block's reference input — the one <c>RunsWorkflowFrom</c> names, the callee a
    /// <c>PortsAreResolvedElsewhere</c> block runs — is fed by a mapping, literal or wired. Parity with the
    /// library's own save gate for <c>SubWorkflow</c> (<c>WorkflowGraphHelpers.ValidateSubWorkflowReferencesStatic</c>),
    /// generalised to any block with such an input: the reference must be a fixed configured value, so the
    /// run's pin manifest — built from configuration alone — is complete by construction, never merely
    /// usually complete for a graph a literal edge could still draw.</summary>
    public const string ReferenceInputMapped = "reference-input-mapped";

    /// <summary>A node lists, in <c>Passthrough</c>, an input that declares <c>SecretValues</c>. The run copies
    /// the input's stored forms into the node's outputs, which no masking on read reaches, so the compile refuses what would leak.</summary>
    public const string SecretInputPassthrough = "secret-input-passthrough";

    /// <summary>A literal of an input that declares <c>SecretValues</c>, in the node's configuration or on an edge, has a key that is no field
    /// name (an RFC 9110 token), as a header written whole as <c>Name: value</c> is. The run refuses such a key, and what compiles runs.</summary>
    public const string SecretInputKeyInvalid = "secret-input-key-invalid";

    /// <summary>A node's own resilience config — <c>timeoutSeconds</c>, <c>retryCount</c>,
    /// <c>retryBackoffSeconds</c>, <c>retryEnabled</c> — carries a value of the wrong kind: a number key
    /// holding something <see cref="System.Text.Json.JsonElement.GetInt32()"/>/<c>GetDouble()</c> cannot
    /// read, or <c>retryEnabled</c> holding anything but a JSON boolean. <c>WorkflowGraphExecutor.ReadResilience</c>
    /// throws on exactly this today, mid-run, possibly after earlier nodes already did their work — except
    /// on a <c>WorkflowInput</c> or <c>WorkflowOutput</c> node, which the executor skips before
    /// <c>ReadResilience</c> ever runs, so this refusal skips them too.</summary>
    public const string ResilienceConfigInvalid = "resilience-config-invalid";

    /// <summary>A node's configuration holds an <c>errorPolicy</c> key, whatever its value. The run reads a node's
    /// policy from the node alone (<c>WorkflowGraphExecutor</c>'s error switch) and hands this key to the block as an
    /// input, so the policy it names never applies.</summary>
    public const string ErrorPolicyInConfiguration = "error-policy-in-configuration";

    /// <summary>A node's error policy sets <c>routeToErrorPort</c> to true. Nothing in the run reads it: under
    /// <c>EmitError</c> a failed node offers <c>_error</c> whatever it holds, and under any other policy it offers none.</summary>
    public const string ErrorPolicyRouteUnread = "error-policy-route-unread";

    /// <summary>A <c>WorkflowInput</c> or <c>WorkflowOutput</c> node stores an <c>errorPolicy</c>, whatever its value. The
    /// run skips these nodes and reads no policy from them, so the one stored never applies.</summary>
    public const string ErrorPolicyOnBoundaryNode = "error-policy-on-boundary-node";

    /// <summary>A node's id is longer than the node log's <c>NodeId</c> column holds
    /// (<see cref="Model.WorkflowGraphHelpers.NodeLogTextLimit"/>). The run writes every node it enters into that
    /// table, and the column is not widened: it is the one the Timescale add-on converts, where an older
    /// TimescaleDB refused the <c>ALTER COLUMN … TYPE</c> a widening is (a newer one takes it — the refusal is
    /// the server's, and the library rests on neither answer). The bound is therefore a contract the graph is
    /// refused against before any write, rather than a step write that fails on the day it is outgrown.</summary>
    public const string NodeIdTooLong = "node-id-too-long";

    /// <summary>A node's block type id is longer than the node log's <c>NodeType</c> column holds — the same
    /// table, the same column width and the same reason as <see cref="NodeIdTooLong"/>. The run writes each
    /// entered node's block type id there, so a block type whose own id does not fit cannot be run at all.
    /// Reachable only where the host's block factory declares such an id.</summary>
    public const string BlockTypeIdTooLong = "block-type-id-too-long";
}
