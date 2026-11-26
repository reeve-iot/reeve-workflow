namespace ReeveWorkflow.Debug;

/// <summary>Where a session's captured state is kept. Snapshots are large and short-lived, so they do not go in
/// the database beside the session row; the host decides what storage that means and under what namespace.</summary>
public interface IWorkflowDebugBlobStore
{
    /// <summary>Stores a blob under a key this store owns. Overwrites: a re-captured snapshot replaces its
    /// predecessor rather than accumulating.</summary>
    Task PutBytesAsync(string key, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>Reads a blob back, or null when the key is unknown — an expired snapshot reads as absent
    /// rather than throwing, because a session outliving its blobs is normal.</summary>
    Task<byte[]?> GetBytesAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Removes a blob. Idempotent: deleting a key nobody holds is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
