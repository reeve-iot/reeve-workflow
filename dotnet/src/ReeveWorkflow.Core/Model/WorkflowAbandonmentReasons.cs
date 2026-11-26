namespace ReeveWorkflow.Core.Model;

/// <summary>Why a resume ended a parked or recovering run instead of walking on. Each code says where it is written: the
/// <c>pinned_version_*</c> codes prefix the run's error message as <c>code: detail</c>; the other two are written as below.
///</summary>
public static class WorkflowAbandonmentReasons
{
    /// <summary>The pinned version's definition does not read as a graph; the detail is the version id.</summary>
    public const string PinnedVersionUnreadable = "pinned_version_unreadable";

    /// <summary>The pinned version is no longer stored; the detail is the version id.</summary>
    public const string PinnedVersionUnavailable = "pinned_version_unavailable";

    /// <summary>The pinned version no longer compiles under the current registry; the detail is the version id and the
    /// rules the compile refused it by.</summary>
    public const string PinnedVersionUncompilable = "pinned_version_uncompilable";

    /// <summary>A value the parked run persisted does not read back as its closed term, or a parked child's answer is missing; the
    /// detail names the payload, the node and the port.</summary>
    public const string ResumeValueInvalid = "resume_value_invalid";

    /// <summary>The journal entry a checkpoint names is gone, so there is nothing to carry the run on under. Written bare, as the
    /// failure reason of the run-state row; no journal entry exists to carry it.</summary>
    public const string JournalEntryMissing = "journal_entry_missing";

    /// <summary>The run's state row had already ended when the resume found it. Prefixes the journal entry's message, whether that
    /// entry ends Failed or Cancelled.</summary>
    public const string RunStateEnded = "run_state_ended";
}
