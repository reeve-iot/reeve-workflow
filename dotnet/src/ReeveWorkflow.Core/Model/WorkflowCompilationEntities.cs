namespace ReeveWorkflow.Core.Model;

/// <summary>One stored <see cref="Compilation.CompilationVerdict"/>. The primary key is the version, the
/// definition digest AND the dependency hash — see <see cref="Compilation.CompilationVerdict"/>'s own remarks
/// for why a pair is not enough once a draft is edited in place.</summary>
public sealed class WorkflowCompilation
{
    public Guid WorkflowVersionId { get; set; }

    public string DefinitionDigest { get; set; } = "";

    public string DependencyHash { get; set; } = "";

    public bool IsValid { get; set; }

    /// <summary>The diagnostics as written by the compiler. Read by the editor without recompiling, so the
    /// reason survives the replica that found it.</summary>
    public string DiagnosticsJson { get; set; } = "[]";

    public string BlockSourcesJson { get; set; } = "[]";

    public string CompilerVersion { get; set; } = "";

    public DateTimeOffset CompiledAtUtc { get; set; }
}
