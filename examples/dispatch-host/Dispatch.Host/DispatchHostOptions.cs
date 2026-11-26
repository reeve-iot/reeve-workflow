namespace Dispatch.Host;

public sealed class DispatchHostOptions
{
    public required string PostgresConnectionString { get; init; }

    public string? RedisConnectionString { get; init; }

    public string ReplicaId { get; init; } = Environment.MachineName;

    public bool HasPeerReplicas { get; init; }

    public TimeSpan AbandonedRunAfter { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan RunHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan RecoverySweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    public int RecoveryBatchSize { get; init; } = 20;

    public TimeSpan WakerPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool MigrateOnStartup { get; init; } = true;
}
