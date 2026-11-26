using Dispatch.Host;

var options = new DispatchHostOptions
{
    PostgresConnectionString = Required("DISPATCH_POSTGRES"),
    RedisConnectionString = Environment.GetEnvironmentVariable("DISPATCH_REDIS"),
    ReplicaId = Environment.GetEnvironmentVariable("DISPATCH_REPLICA_ID") ?? Environment.MachineName,
    HasPeerReplicas = Flag("DISPATCH_HAS_PEER_REPLICAS"),
    AbandonedRunAfter = Seconds("DISPATCH_ABANDONED_RUN_AFTER_SECONDS", TimeSpan.FromMinutes(1)),
    RunHeartbeatInterval = Seconds("DISPATCH_RUN_HEARTBEAT_SECONDS", TimeSpan.FromSeconds(10)),
    RecoverySweepInterval = Seconds("DISPATCH_RECOVERY_SWEEP_SECONDS", TimeSpan.FromSeconds(30)),
    WakerPollInterval = Seconds("DISPATCH_WAKER_POLL_SECONDS", TimeSpan.FromSeconds(5)),
    ShutdownTimeout = Seconds("DISPATCH_SHUTDOWN_TIMEOUT_SECONDS", TimeSpan.FromSeconds(30))
};

await DispatchHost.Build(options, args).RunAsync();

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"The environment variable {name} is required.");

static bool Flag(string name) =>
    bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value;

static TimeSpan Seconds(string name, TimeSpan fallback) =>
    double.TryParse(Environment.GetEnvironmentVariable(name), out var value)
        ? TimeSpan.FromSeconds(value)
        : fallback;
