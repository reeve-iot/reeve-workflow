namespace ReeveWorkflow.Abstractions;

public interface IWorkflowBlockFactory
{
    string BlockTypeId { get; }
    string DisplayName { get; }
    string Description { get; }
    string Category { get; }
    IReadOnlyCollection<BlockPortDefinition> Inputs { get; }
    IReadOnlyCollection<BlockPortDefinition> Outputs { get; }
    /// <summary>True when a node of this block may hold a port list under the <c>InputPorts</c> or <c>OutputPorts</c>
    /// configuration keys. Left false, a node holding a non-empty list is refused (<c>port-list-without-dynamic-ports</c>).</summary>
    bool HasDynamicPorts => false;

    /// <summary>True for blocks whose author may DECLARE typed output ports and map each sub-canvas onto them.
    /// Unlike <see cref="HasDynamicPorts"/> this keeps the block's static ports. Left false, a node of the block that declares
    /// outputs is refused (<c>declared-outputs-unsupported</c>); a declared output may take its source only from
    /// one of <see cref="SubCanvasNames"/> (<c>declared-output-source-unknown</c>).</summary>
    bool SupportsDeclaredOutputs => false;

    /// <summary>True for a block that runs its one sub-canvas once per iteration and collects each declared output across the
    /// iterations: a declared output <c>X</c> of stored type <c>T</c> becomes the node output <c>X: Collection&lt;T?&gt;</c> (<c>Collection&lt;T&gt;</c>
    /// when <c>T</c> is already nullable), one element per iteration in order, null where an iteration left <c>X</c> absent. The body's
    /// <c>WorkflowOutput</c> takes <c>T</c> under the key <c>sources</c> names. A stored <c>T</c> holding <c>Json</c> is refused
    /// (<c>port-type-json-position</c>). The block builds each collection through <see cref="WorkflowBlockContext.Types"/> and reads the
    /// names to build from <see cref="WorkflowBlockContext.DeclaredOutputs"/>. Must come with <see cref="SupportsDeclaredOutputs"/> and
    /// exactly one sub-canvas, else the catalogue read refuses the block (<c>block-port-declaration-invalid</c>).</summary>
    bool DeclaredOutputsCollect => false;
    IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema => [];
    IReadOnlyCollection<string> SubCanvasNames => [];
    IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName) => [];
    IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string subCanvasName) => [];

    /// <summary>Whether re-invoking this block after a crash produces the same observable outcome; false makes the
    /// engine fail the execution instead of retrying. ADVISORY: the host never verifies the claim.</summary>
    bool IsIdempotent => false;

    /// <summary>True for a container that does nothing of its own: its effect is exactly what its sub-canvases
    /// hold, and which of them it calls, with what, and how many times is decided only by its inputs and by what
    /// those calls returned. A recovery may then replay the calls that finished and re-enter the one that did not.</summary>
    /// <remarks>Separate from <see cref="IsIdempotent"/>, which answers about a retry: a loop retried restarts its
    /// body from the first iteration, so every loop answers no there and still belongs here. ADVISORY, like
    /// <see cref="IsIdempotent"/> — a container that consults a clock or a live service to decide how many times to
    /// call its sub-canvas, or that sends anything itself, must leave this false.</remarks>
    bool OnlyDrivesItsSubCanvases => false;

    /// <summary>True for a block that may park the run rather than returning: what it is waiting for is
    /// answered from outside the process, and the run is put back where it was when it is.
    /// ADVISORY, like <see cref="IsIdempotent"/>: it is what the Editor refuses a placement by, and a block
    /// that leaves it false is still refused by the run when it parks somewhere nothing can re-enter. Left
    /// false, an author is told by a run that failed instead of while they are still looking at the graph.</summary>
    /// <remarks>What the editor refuses a placement on: a block that may park cannot sit inside a container that
    /// does not declare <see cref="OnlyDrivesItsSubCanvases"/>, because nothing could put the run back inside it.
    /// The engine refuses the same placement when the run reaches it, by the block actually parking; this is that
    /// refusal moved to the save, where an author can still do something about it. A durable sleep is not one of
    /// these: where it cannot park it is served in the process, which is slower and not wrong.</remarks>
    bool CanSuspend => false;

    /// <summary>True for a block whose execution is more dangerous than its inputs suggest — arbitrary code, say — so that a workflow containing one runs only for someone the host trusts with it.</summary>
    /// <remarks>Deliberately a flag and not a permission code: the block says what it is, the host says who may run one, and no block ever names an RBAC code it cannot see the meaning of.</remarks>
    bool IsPrivileged => false;

    /// <summary>The type parameters this block's port types may name as <c>$Name</c> (<see cref="PortValueType.OfDeclaration"/>).
    /// A parameter that stands in a sub-canvas output is bound by what the body produces and may not stand on an input
    /// or a sub-canvas input. Declared in order, each name once. Refused when the catalogue is read
    /// (<c>block-port-declaration-invalid</c>) if a port names an undeclared parameter or a constraint is unknown.</summary>
    IReadOnlyList<WorkflowTypeParameter> TypeParameters => [];

    /// <summary>Where this block came from, in the host's own vocabulary — a plugin id and version, an
    /// assembly name, anything. Opaque: the library stores it and repeats it, and never parses it.</summary>
    string? Source => null;

    /// <summary>True when this block's real ports are not <see cref="Inputs"/>/<see cref="Outputs"/>: they
    /// belong to a workflow it runs, or to something only a host resolver can see. A node of such a block
    /// is refused unless one answers for it — "I do not know this node's signature" is never waved through.</summary>
    bool PortsAreResolvedElsewhere => false;

    /// <summary>When <see cref="PortsAreResolvedElsewhere"/> names its answer through one input's value —
    /// mirroring the editor's own <c>runsWorkflowFrom</c> — that input's name. The compiler reads the callee
    /// from this input's configured value only, and refuses any mapping into it, a wire or a literal. Null
    /// for a block resolved some other way, such as a host's discovery seam with no such input to check.</summary>
    string? RunsWorkflowFrom => null;

    IWorkflowBlockPlugin Create();
}
