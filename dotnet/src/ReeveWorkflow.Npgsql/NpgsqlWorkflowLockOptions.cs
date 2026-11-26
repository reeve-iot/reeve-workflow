using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ReeveWorkflow.Npgsql;

/// <summary>What this package needs to take a lock: where the session goes, and what the key is called once
/// PostgreSQL sees it.</summary>
public sealed class NpgsqlWorkflowLockOptions
{
    /// <summary>The connection this package opens its own session on. Required.</summary>
    /// <remarks>Its own, not the application's: an advisory lock belongs to the session that took it, so a
    /// pooled connection handed back between calls would release every lock the host still believes it holds.
    /// One session per process, held open, is the shape that makes the lease mean something.</remarks>
    public string? ConnectionString { get; set; }

    /// <summary>Prepended to every key before it is derived. Empty by default, which is what the first
    /// adopter's replicas already hold locks under.</summary>
    /// <remarks>Set it when two deployments share one database. Advisory locks have one namespace per
    /// database and the library's own keys are constants, so an unprefixed staging deployment and an
    /// unprefixed production one take each other's retention leadership without either noticing.</remarks>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Turns a lock key into the 64-bit number the advisory lock is taken on.</summary>
    /// <remarks>A cross-replica contract, and a cross-<em>application</em> one: advisory locks share one
    /// namespace per database, so two deployments on one database must not derive the same number from
    /// different keys. The default is SHA-256 of the key, first eight bytes, little-endian — what the first
    /// adopter's replicas are holding, so adopting this package renames none of their locks.</remarks>
    public Func<string, long> LockId { get; set; } = DeriveLockId;

    /// <summary>The default derivation, exposed so a host can compose it or compare against it.</summary>
    public static long DeriveLockId(string lockKey)
    {
        ArgumentNullException.ThrowIfNull(lockKey);
        return BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(lockKey)));
    }
}
