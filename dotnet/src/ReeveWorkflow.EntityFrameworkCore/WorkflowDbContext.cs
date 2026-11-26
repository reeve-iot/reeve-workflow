using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>The workflow tables, mapped by the library rather than by its host. Same schema, same table and
/// column names as the host's own context; the two map the same rows and neither knows about the other.</summary>
public class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options) : DbContext(options)
{
    public DbSet<Workflow> Workflows => Set<Workflow>();

    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();

    public DbSet<WorkflowExecutionLog> WorkflowExecutionLogs => Set<WorkflowExecutionLog>();

    public DbSet<WorkflowExecutionLogEntry> WorkflowExecutionLogEntries => Set<WorkflowExecutionLogEntry>();

    public DbSet<WorkflowExecutionStep> WorkflowExecutionSteps => Set<WorkflowExecutionStep>();

    public DbSet<WorkflowExecutionState> WorkflowExecutionStates => Set<WorkflowExecutionState>();

    public DbSet<WorkflowSuspension> WorkflowSuspensions => Set<WorkflowSuspension>();

    public DbSet<WorkflowSuspensionDependencyKey> WorkflowSuspensionDependencyKeys => Set<WorkflowSuspensionDependencyKey>();

    public DbSet<WorkflowDebugSession> WorkflowDebugSessions => Set<WorkflowDebugSession>();

    public DbSet<WorkflowDebugSnapshot> WorkflowDebugSnapshots => Set<WorkflowDebugSnapshot>();

    public DbSet<WorkflowDebugBlob> WorkflowDebugBlobs => Set<WorkflowDebugBlob>();

    public DbSet<WorkflowRestartGeneration> WorkflowRestartGenerations => Set<WorkflowRestartGeneration>();

    public DbSet<WorkflowRestartWaiter> WorkflowRestartWaiters => Set<WorkflowRestartWaiter>();

    public DbSet<WorkflowCompilation> WorkflowCompilations => Set<WorkflowCompilation>();

    private const int RunStateFillFactor = 70;

    private const string PostgresStorageParameter = "Npgsql:StorageParameter:";

    private static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> HostMetadataConverter = new(
        map => JsonSerializer.Serialize(map, (JsonSerializerOptions?)null),
        json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, (JsonSerializerOptions?)null)!);

    private static readonly ValueComparer<IReadOnlyDictionary<string, string>> HostMetadataComparer = new(
        (left, right) => SameMetadata(left, right),
        map => map.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
        map => new Dictionary<string, string>(map, StringComparer.Ordinal));

    private static bool SameMetadata(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right) =>
        ReferenceEquals(left, right)
        || (left is not null && right is not null && left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Workflow>(builder =>
        {
            builder.ToTable("workflows", "automation");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Id).HasColumnName("id");
            builder.Property(entity => entity.Name).HasColumnName("name").HasColumnType("text").IsRequired();
            builder.Property(entity => entity.Description).HasColumnName("description").HasColumnType("text");
            builder.Property(entity => entity.ExecutionTimeoutSeconds).HasColumnName("execution_timeout_seconds");
            builder.Property(entity => entity.RetryPolicyJson).HasColumnName("retry_policy_json").HasColumnType("text");
            builder.Property(entity => entity.CheckpointPolicyJson)
                .HasColumnName("checkpoint_policy_json")
                .HasColumnType("text");
            builder.Property(entity => entity.Mode)
                .HasColumnName("mode")
                .HasConversion<int>()
                .IsRequired()
                .HasDefaultValue(WorkflowMode.Single);
            builder.Property(entity => entity.MaxConcurrentRuns)
                .HasColumnName("max_concurrent_runs")
                .IsRequired()
                .HasDefaultValue(1);
            builder.Property(entity => entity.MaxQueuedRuns)
                .HasColumnName("max_queued_runs")
                .IsRequired()
                .HasDefaultValue(10);
            builder.Property(entity => entity.OverflowBehavior)
                .HasColumnName("overflow_behavior")
                .HasConversion<int>()
                .IsRequired()
                .HasDefaultValue(WorkflowOverflowBehavior.Drop);
            builder.Property(entity => entity.WaitPollIntervalSeconds)
                .HasColumnName("wait_poll_interval_seconds");
            builder.Property(entity => entity.HasImportWarnings).HasColumnName("has_import_warnings").IsRequired().HasDefaultValue(false);
            builder.Property(entity => entity.ImportWarningsJson).HasColumnName("import_warnings_json").HasColumnType("jsonb");
            builder.Property(entity => entity.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
            builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
            builder.Property(entity => entity.HostMetadata)
                .HasColumnName("host_metadata")
                .HasColumnType("jsonb")
                .HasConversion(HostMetadataConverter, HostMetadataComparer);
            builder.HasIndex(entity => entity.CreatedByUserId);
            builder.HasOne(entity => entity.CurrentVersion)
                .WithMany()
                .HasForeignKey(entity => entity.CurrentVersionId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.Property(entity => entity.CurrentVersionId).HasColumnName("current_version_id");

            builder.HasOne(entity => entity.DraftVersion)
                .WithMany()
                .HasForeignKey(entity => entity.DraftVersionId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.Property(entity => entity.DraftVersionId).HasColumnName("draft_version_id");

            builder.Property(entity => entity.Revision).HasColumnName("revision").IsRequired().HasDefaultValue(0);
        });

        modelBuilder.Entity<WorkflowVersion>(builder =>
        {
            builder.ToTable("workflow_versions", "automation");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Id).HasColumnName("id");
            builder.Property(entity => entity.WorkflowId).HasColumnName("workflow_id");
            builder.Property(entity => entity.VersionNumber).HasColumnName("version_number").IsRequired();
            builder.Property(entity => entity.DefinitionJson).HasColumnName("definition_json").HasColumnType("jsonb").IsRequired();
            builder.Property(entity => entity.InputParametersJson).HasColumnName("input_parameters_json").HasColumnType("jsonb");
            builder.Property(entity => entity.OutputParametersJson).HasColumnName("output_parameters_json").HasColumnType("jsonb");
            builder.Property(entity => entity.ChangeMessage).HasColumnName("change_message").HasColumnType("text");
            builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            builder.Property(entity => entity.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
            builder.Property(entity => entity.IsPublished).HasColumnName("is_published").IsRequired();
            builder.Property(entity => entity.PublishedAtUtc).HasColumnName("published_at_utc");

            builder.HasOne(entity => entity.Workflow)
                .WithMany()
                .HasForeignKey(entity => entity.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(entity => entity.CreatedByUserId);
            builder.HasIndex(entity => new { entity.WorkflowId, entity.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<WorkflowExecutionLog>(builder =>
        {
            builder.ToTable("workflow_execution_logs", "automation");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Id).HasColumnName("id");
            builder.Property(entity => entity.Status).HasColumnName("status").IsRequired();
            builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
            builder.Property(entity => entity.CompletedAtUtc).HasColumnName("completed_at_utc");
            builder.Property(entity => entity.AttemptNumber).HasColumnName("attempt_number").IsRequired().HasDefaultValue(1);
            builder.Property(entity => entity.ErrorMessage).HasColumnName("error_message").HasMaxLength(2000);
            builder.Property(entity => entity.OutputsJson).HasColumnName("outputs_json").HasColumnType("jsonb");
            builder.Property(entity => entity.ResultOutputsJson).HasColumnName("result_outputs_json").HasColumnType("jsonb");
            builder.Property(entity => entity.FailureReason).HasColumnName("failure_reason").HasMaxLength(64);
            builder.Property(entity => entity.AutomationId).HasColumnName("automation_id");

            builder.HasOne(entity => entity.Workflow)
                .WithMany()
                .HasForeignKey(entity => entity.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Property(entity => entity.WorkflowId).HasColumnName("workflow_id");

            builder.HasOne(entity => entity.WorkflowVersion)
                .WithMany()
                .HasForeignKey(entity => entity.WorkflowVersionId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Property(entity => entity.WorkflowVersionId).HasColumnName("workflow_version_id");

            builder.Property(entity => entity.TriggerId).HasColumnName("trigger_id");
            builder.HasIndex(entity => entity.TriggerId);
            builder.HasIndex(entity => new { entity.AutomationId, entity.StartedAtUtc });
        });

        modelBuilder.Entity<WorkflowExecutionLogEntry>(builder =>
        {
            builder.ToTable("workflow_execution_log_entries", "automation");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.NodeId).HasColumnType("text").IsRequired();
            builder.Property(entity => entity.BlockTypeId).HasColumnType("text").IsRequired();
            builder.Property(entity => entity.Level).IsRequired();
            builder.Property(entity => entity.Message).HasMaxLength(4000).IsRequired();
            builder.Property(entity => entity.DataJson).HasColumnType("jsonb");
            builder.Property(entity => entity.CallStackJson).HasColumnType("jsonb");
            builder.Property(entity => entity.Timestamp).IsRequired();
            builder.HasOne(entity => entity.ExecutionLog)
                .WithMany(entity => entity.Entries)
                .HasForeignKey(entity => entity.ExecutionLogId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(entity => new { entity.ExecutionLogId, entity.Timestamp });
        });

        modelBuilder.Entity<WorkflowExecutionStep>(builder =>
        {
            builder.ToTable("execution_node_log", "automation");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.NodeId).HasMaxLength(WorkflowGraphHelpers.NodeLogTextLimit).IsRequired();
            builder.Property(entity => entity.NodeType).HasMaxLength(WorkflowGraphHelpers.NodeLogTextLimit).IsRequired();
            builder.Property(entity => entity.StartedAt).IsRequired();
            builder.Property(entity => entity.EndedAt).IsRequired();
            builder.Property(entity => entity.DurationMs).IsRequired();
            builder.Property(entity => entity.Status).IsRequired();
            builder.Property(entity => entity.SortOrder).IsRequired();
            builder.Property(entity => entity.InputsJson).HasColumnType("jsonb");
            builder.Property(entity => entity.OutputsJson).HasColumnType("jsonb");
            builder.Property(entity => entity.ErrorMessage).HasMaxLength(2000);
            builder.Property(entity => entity.ErrorStackTrace).HasMaxLength(8000);
            builder.Property(entity => entity.ErrorJson).HasColumnType("jsonb");
            builder.HasOne(entity => entity.ExecutionLog)
                .WithMany(entity => entity.Steps)
                .HasForeignKey(entity => entity.ExecutionLogId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(entity => new { entity.ExecutionLogId, entity.SortOrder });
        });

        modelBuilder.Entity<WorkflowExecutionState>(builder =>
        {
            builder.ToTable("workflow_executions_state", "automation", table => table.HasCheckConstraint(
                "CK_workflow_executions_state_status",
                "status IN ('Pending','Running','Sleeping','Waiting','Completed','Failed','Cancelled','Suspended')"));
            builder.HasKey(e => e.ExecutionId);
            builder.Property(e => e.ExecutionId).HasColumnName("execution_id");
            builder.Property(e => e.WorkflowId).HasColumnName("workflow_id").IsRequired();
            builder.Property(e => e.WorkflowVersion).HasColumnName("workflow_version").IsRequired();
            builder.Property(e => e.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
            builder.Property(e => e.CurrentBlockPath).HasColumnName("current_block_path").HasMaxLength(500).IsRequired();
            builder.Property(e => e.VariablesJson).HasColumnName("variables_json").HasColumnType("jsonb").IsRequired();
            builder.Property(e => e.WaitUntilUtc).HasColumnName("wait_until_utc");
            builder.Property(e => e.WaitForEventKey).HasColumnName("wait_for_event_key").HasMaxLength(200);
            builder.Property(e => e.OwnedByReplicaId).HasColumnName("owned_by_replica_id")
                .HasMaxLength(WorkflowHostDefaultsOptions.ReplicaIdColumnWidth);
            builder.Property(e => e.LastTransitionUtc).HasColumnName("last_transition_utc").IsRequired();
            builder.Property(e => e.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
            builder.Property(e => e.CompletedAtUtc).HasColumnName("completed_at_utc");
            builder.Property(e => e.FailureReason).HasColumnName("failure_reason").HasMaxLength(2000);
            builder.Property(e => e.TriggerSource).HasColumnName("trigger_source").HasMaxLength(200);
            builder.Property(e => e.Version).HasColumnName("version").IsRequired();
            builder.Property(e => e.PinnedVersionsJson).HasColumnName("pinned_versions_json").HasColumnType("jsonb");
            builder.Property(e => e.ParentExecutionId).HasColumnName("parent_execution_id");

            builder.HasOne(e => e.Workflow)
                .WithMany()
                .HasForeignKey(e => e.WorkflowId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(e => new { e.Status, e.WaitUntilUtc })
                .HasDatabaseName("ix_workflow_executions_state_status_wait_until");

            builder.HasIndex(e => new { e.WorkflowId, e.Status })
                .HasDatabaseName("ix_workflow_executions_state_workflow_status");

            builder.HasIndex(e => new { e.WorkflowId, e.StartedAtUtc, e.ExecutionId })
                .HasDatabaseName("ix_workflow_executions_state_workflow_started");

            builder.Metadata.SetAnnotation(PostgresStorageParameter + "fillfactor", RunStateFillFactor);

            builder.HasIndex(e => e.WaitForEventKey)
                .HasFilter("\"wait_for_event_key\" IS NOT NULL")
                .HasDatabaseName("ix_workflow_executions_state_event_key");
        });

        modelBuilder.Entity<WorkflowRestartGeneration>(builder =>
        {
            builder.ToTable("workflow_restart_generations", "automation");
            builder.HasKey(entity => entity.WorkflowId);
            builder.Property(entity => entity.WorkflowId).HasColumnName("workflow_id");
            builder.Property(entity => entity.Issued).HasColumnName("issued").IsRequired();
            builder.Property(entity => entity.Started).HasColumnName("started").IsRequired();
        });

        modelBuilder.Entity<WorkflowRestartWaiter>(builder =>
        {
            builder.ToTable("workflow_restart_waiters", "automation");
            builder.HasKey(entity => new { entity.WorkflowId, entity.Generation });
            builder.Property(entity => entity.WorkflowId).HasColumnName("workflow_id");
            builder.Property(entity => entity.Generation).HasColumnName("generation");
            builder.Property(entity => entity.LiveUntilUnixMs).HasColumnName("live_until_unix_ms").IsRequired();
        });

        modelBuilder.Entity<WorkflowSuspension>(builder =>
        {
            builder.ToTable("workflow_suspensions", "automation");
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Id).HasColumnName("id");
            builder.Property(e => e.ExecutionLogId).HasColumnName("execution_log_id").IsRequired();
            builder.Property(e => e.WorkflowId).HasColumnName("workflow_id").IsRequired();
            builder.Property(e => e.BlockId).HasColumnName("block_id").HasMaxLength(500).IsRequired();
            builder.Property(e => e.Kind)
                .HasColumnName("kind")
                .HasConversion<int>()
                .IsRequired();
            builder.Property(e => e.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
            builder.Property(e => e.ConfigJson).HasColumnName("config_json").HasColumnType("jsonb").IsRequired();
            builder.Property(e => e.StatePayloadJson).HasColumnName("state_payload_json").HasColumnType("jsonb").IsRequired();
            builder.Property(e => e.SuspendedAtUtc).HasColumnName("suspended_at_utc").IsRequired();
            builder.Property(e => e.TimeoutAtUtc).HasColumnName("timeout_at_utc").IsRequired();
            builder.Property(e => e.ResolvedAtUtc).HasColumnName("resolved_at_utc");
            builder.Property(e => e.OutcomeJson).HasColumnName("outcome_json").HasColumnType("text");
            builder.Property(e => e.TakeCount).HasColumnName("take_count").IsRequired().HasDefaultValue(0);

            builder.HasOne(e => e.ExecutionLog)
                .WithMany()
                .HasForeignKey(e => e.ExecutionLogId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(e => e.Workflow)
                .WithMany()
                .HasForeignKey(e => e.WorkflowId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(e => new { e.Status, e.TimeoutAtUtc })
                .HasDatabaseName("ix_workflow_suspensions_status_timeout");

            builder.HasIndex(e => new { e.SuspendedAtUtc, e.Id })
                .HasFilter(ParkedWaitKinds.SpeculativeIndexFilter)
                .HasDatabaseName("ix_workflow_suspensions_parked_speculative");

            builder.HasIndex(e => new { e.ResolvedAtUtc, e.Id })
                .HasFilter("\"status\" IN ('Resumed', 'TimedOut')")
                .HasDatabaseName("ix_workflow_suspensions_taken");

            builder.HasIndex(e => e.ExecutionLogId)
                .HasDatabaseName("ix_workflow_suspensions_execution_log_id");
        });

        modelBuilder.Entity<WorkflowSuspensionDependencyKey>(builder =>
        {
            builder.ToTable("workflow_suspension_dependency_keys", "automation");
            builder.HasKey(row => new { row.SuspensionId, row.DependencyKey });
            builder.Property(row => row.SuspensionId).HasColumnName("suspension_id");
            builder.Property(row => row.DependencyKey).HasColumnName("dependency_key").HasColumnType("text");
            builder.HasOne<WorkflowSuspension>().WithMany().HasForeignKey(row => row.SuspensionId).OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(row => new { row.DependencyKey, row.SuspensionId }).HasDatabaseName("ix_workflow_suspension_dependency_keys_key");
        });

        modelBuilder.Entity<WorkflowDebugSession>(builder =>
        {
            builder.ToTable("workflow_debug_sessions", "automation");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Id).HasColumnName("id");
            builder.Property(entity => entity.WorkflowId).HasColumnName("workflow_id").IsRequired();
            builder.Property(entity => entity.WorkflowExecutionLogId).HasColumnName("workflow_execution_log_id");
            builder.Property(entity => entity.OwnerUserId).HasColumnName("owner_user_id").IsRequired();
            builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<int>().IsRequired();
            builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            builder.Property(entity => entity.LastActivityUtc).HasColumnName("last_activity_utc").IsRequired();

            builder.HasOne(entity => entity.Workflow)
                .WithMany()
                .HasForeignKey(entity => entity.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne<WorkflowExecutionLog>()
                .WithMany()
                .HasForeignKey(entity => entity.WorkflowExecutionLogId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.HasIndex(entity => entity.OwnerUserId);
            builder.HasIndex(entity => entity.WorkflowExecutionLogId);
        });

        modelBuilder.Entity<WorkflowDebugSnapshot>(builder =>
        {
            builder.ToTable("workflow_debug_snapshots", "automation");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Id).HasColumnName("id");
            builder.Property(entity => entity.DebugSessionId).HasColumnName("debug_session_id").IsRequired();
            builder.Property(entity => entity.NodeId).HasColumnName("node_id").HasColumnType("text").IsRequired();
            builder.Property(entity => entity.CapturedAtUtc).HasColumnName("captured_at_utc").IsRequired();

            builder.HasOne(entity => entity.DebugSession)
                .WithMany(entity => entity.Snapshots)
                .HasForeignKey(entity => entity.DebugSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(entity => entity.DebugSessionId);
        });

        modelBuilder.Entity<WorkflowDebugBlob>(builder =>
        {
            builder.ToTable("workflow_debug_blobs", "automation");
            builder.HasKey(entity => entity.Key);
            builder.Property(entity => entity.Key).HasColumnName("key").HasMaxLength(200);
            builder.Property(entity => entity.Content).HasColumnName("content").IsRequired();
            builder.Property(entity => entity.WrittenAtUtc).HasColumnName("written_at_utc").IsRequired();
        });

        modelBuilder.Entity<WorkflowCompilation>(builder =>
        {
            builder.ToTable("workflow_compilations", "automation");
            builder.HasKey(entity => new { entity.WorkflowVersionId, entity.DefinitionDigest, entity.DependencyHash });
            builder.Property(entity => entity.WorkflowVersionId).HasColumnName("workflow_version_id");
            builder.Property(entity => entity.DefinitionDigest).HasColumnName("definition_digest").HasMaxLength(64);
            builder.Property(entity => entity.DependencyHash).HasColumnName("dependency_hash").HasMaxLength(64);
            builder.Property(entity => entity.IsValid).HasColumnName("is_valid").IsRequired();
            builder.Property(entity => entity.DiagnosticsJson).HasColumnName("diagnostics_json").HasColumnType("jsonb").IsRequired();
            builder.Property(entity => entity.BlockSourcesJson).HasColumnName("block_sources_json").HasColumnType("jsonb").IsRequired();
            builder.Property(entity => entity.CompilerVersion).HasColumnName("compiler_version").HasMaxLength(100).IsRequired();
            builder.Property(entity => entity.CompiledAtUtc).HasColumnName("compiled_at_utc").IsRequired()
                .HasConversion(
                    instant => instant.UtcDateTime,
                    stored => new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc)));
        });
    }
}
