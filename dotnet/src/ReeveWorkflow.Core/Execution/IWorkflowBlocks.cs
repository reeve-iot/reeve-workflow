using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Execution;

/// <summary>The blocks a host has: the graph walk's lookup and the palette's enumeration are two questions
/// about one collection, and a host answering one has answered the other.</summary>
/// <remarks>The host owns registration, hot re-registration and the shadowing rules; none of that is the
/// engine's business while it is walking a graph.</remarks>
public interface IWorkflowBlocks
{
    /// <summary>The factory registered under this block type id, or null when nothing is. Null is a definition
    /// error the walk turns into <see cref="InvalidBlockTypeException"/>, never a reason to skip the node.</summary>
    /// <remarks>Ids match case-insensitively (ordinal, ignoring case), as <c>RegisteredWorkflowBlocks</c> and the
    /// compiler's catalogue match them. <see cref="WorkflowDefinitionBlockTypes.Collect"/> returns one casing per id
    /// on that contract, so a host whose lookup is case-sensitive breaks the privileged-block gate.</remarks>
    IWorkflowBlockFactory? Find(string blockTypeId);

    /// <summary>Whether this definition names a block that declared itself privileged, sub-canvases included.
    /// An unregistered id counts as not privileged — such a run fails on that node anyway, so the gate never guesses.</summary>
    /// <remarks>The engine does not call this overload: admission asks the set overload, so a host that
    /// customised this one is bypassed there.</remarks>
    bool ContainsPrivilegedBlock(string? definitionJson);

    /// <summary>Checks cached definition block ids against the live registry after hot re-registration.
    /// An unregistered id is not privileged; the run will fail when it reaches that node.</summary>
    bool ContainsPrivilegedBlock(IReadOnlySet<string> blockTypeIds) =>
        blockTypeIds.Any(id => Find(id)?.IsPrivileged == true);

    /// <summary>Every registered factory. A reader takes whatever is registered at the moment it asks and must
    /// not cache across a re-registration — except by <see cref="Generation"/>, which a re-registering host
    /// must bump no earlier than the moment this method starts answering with the new set.</summary>
    IReadOnlyCollection<IWorkflowBlockFactory> GetAll();

    /// <summary>Changes when the registered set of blocks does. Bump it no earlier than the new set is
    /// already visible from <see cref="GetAll"/>: a bump that runs first leaves a caller holding a snapshot
    /// labelled with the new generation but built from the old catalogue until whatever bumped it finishes.
    /// A host that installs blocks only through a restart never moves it and keeps the default; one that
    /// loads them live must override it, or a cached compilation verdict outlives its catalogue.</summary>
    string Generation => "static";
}
