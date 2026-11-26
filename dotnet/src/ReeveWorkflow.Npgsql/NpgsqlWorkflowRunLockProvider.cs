using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;
using Reeve.EntityFrameworkCore.AdvisoryLocks;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Npgsql;

/// <summary>The one-permit modes' cluster gate over PostgreSQL advisory locks, on one session this process
/// keeps open. A session that dies releases everything it held, which is the failover this design wants.</summary>
/// <remarks>There is no TTL and nothing to renew: the lock lives exactly as long as the session, so a killed
/// replica frees its runs at once and a stalled one keeps them until its connection actually goes. That is
/// why a lease reports itself invalid by proving the session still answers, rather than by reading a clock.
///
/// Advisory locks are re-entrant within a session, so a shared session would hand the same key to two callers
/// in one process. The held-key set is what refuses the second one.</remarks>
public sealed class NpgsqlWorkflowRunLockProvider : IWorkflowRunLockProvider, IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private const int KeepAliveSeconds = 30;

    private readonly string connectionString;
    private readonly string keyPrefix;
    private readonly Func<string, long> lockIdOf;
    private readonly ILogger logger;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<long> heldKeys = [];
    private NpgsqlConnection? connection;
    private long generation;
    private bool disposed;

    /// <summary>Takes the connection this process locks on and the derivation its replicas agree about.</summary>
    /// <param name="options">Required, and its <see cref="NpgsqlWorkflowLockOptions.ConnectionString"/> with it.</param>
    /// <param name="logger">Where a lost session is reported. Lock keys are the library's own and carry no
    /// host data, so they are logged whole rather than fingerprinted.</param>
    public NpgsqlWorkflowRunLockProvider(NpgsqlWorkflowLockOptions options, ILogger<NpgsqlWorkflowRunLockProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        connectionString = string.IsNullOrWhiteSpace(options.ConnectionString)
            ? throw new InvalidOperationException(
                "Seams.RunLockProvider.UseNpgsqlLocks needs a connection string. This package opens a session of "
                + "its own to hold advisory locks on; there is nothing sensible to fall back to.")
            : SessionConnectionString(options.ConnectionString);
        keyPrefix = options.KeyPrefix ?? string.Empty;
        lockIdOf = options.LockId ?? NpgsqlWorkflowLockOptions.DeriveLockId;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockKey);
        var lockId = lockIdOf(keyPrefix + lockKey);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var takenOn = await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

            if (heldKeys.Contains(lockId))
            {
                if (await SessionAnswersAsync().ConfigureAwait(false))
                    return null;

                takenOn = await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!await AdvisorySessionLock.TryLockAsync(
                    connection!, AdvisoryLockKey.FromRaw(lockId), commandTimeout: CommandTimeout,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false))
                return null;

            heldKeys.Add(lockId);
            return new AdvisoryLock(this, lockId, takenOn);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            logger.LogWarning(ex, "Could not take the advisory lock for {LockKey}.", lockKey);
            await UndoPossibleGrantAsync(lockId).ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> IsHeldAsync(long takenOn, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed || connection is not { State: ConnectionState.Open } || generation != takenOn)
                return false;

            return await SessionAnswersAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> SessionAnswersAsync()
    {
        try
        {
            await using var command = connection!.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = (int)CommandTimeout.TotalSeconds;
            await command.ExecuteScalarAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The session holding this process's advisory locks is gone; its leases are void.");
            await DropConnectionAsync().ConfigureAwait(false);
            return false;
        }
    }

    private async Task ReleaseAsync(long takenOn, long lockId)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || connection is not { State: ConnectionState.Open } || generation != takenOn)
                return;

            bool unlocked;
            try
            {
                unlocked = await UnlockAsync(lockId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not release advisory lock {LockId}; asking its session again.", lockId);
                if (!await SessionAnswersAsync().ConfigureAwait(false))
                    return;
                unlocked = await UnlockAsync(lockId).ConfigureAwait(false);
            }

            heldKeys.Remove(lockId);

            if (!unlocked)
            {
                logger.LogWarning(
                    "Advisory lock {LockId} was not held by this session after all; dropping the session.", lockId);
                await DropConnectionAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not release advisory lock {LockId} again; dropping its session instead.", lockId);
            await DropConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> UnlockAsync(long lockId)
    {
        return await AdvisorySessionLock.UnlockAsync(
            connection!, AdvisoryLockKey.FromRaw(lockId), commandTimeout: CommandTimeout).ConfigureAwait(false);
    }

    private async Task UndoPossibleGrantAsync(long lockId)
    {
        if (connection is not { State: ConnectionState.Open })
        {
            await DropConnectionAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await AdvisorySessionLock.UnlockAsync(
                connection, AdvisoryLockKey.FromRaw(lockId), commandTimeout: CommandTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not give back advisory lock {LockId}; dropping the session.", lockId);
            await DropConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<long> EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (connection is { State: ConnectionState.Open })
            return generation;

        await DropConnectionAsync().ConfigureAwait(false);
        connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return ++generation;
    }

    private async Task DropConnectionAsync()
    {
        if (connection is null)
            return;

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Closing the advisory-lock session failed; it is being discarded anyway.");
        }

        connection = null;
        heldKeys.Clear();
    }

    internal static string SessionConnectionString(string configured)
    {
        var builder = new NpgsqlConnectionStringBuilder(configured) { Pooling = false };
        if (builder.KeepAlive <= 0)
            builder.KeepAlive = KeepAliveSeconds;

        if (builder.ContainsKey(MultiplexingKeyword))
            builder.Remove(MultiplexingKeyword);

        return builder.ConnectionString;
    }

    private const string MultiplexingKeyword = "Multiplexing";

    /// <summary>Closes the session, which releases every lock this process still holds.</summary>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            disposed = true;
            await DropConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed class AdvisoryLock(NpgsqlWorkflowRunLockProvider owner, long lockId, long takenOn) : IWorkflowRunLock
    {
        private int released;

        public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) =>
            Volatile.Read(ref released) != 0
                ? Task.FromResult(false)
                : owner.IsHeldAsync(takenOn, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
                return;

            await owner.ReleaseAsync(takenOn, lockId).ConfigureAwait(false);
        }
    }
}
