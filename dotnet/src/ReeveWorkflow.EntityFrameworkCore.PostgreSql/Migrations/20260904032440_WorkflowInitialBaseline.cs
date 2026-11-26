using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReeveWorkflow.EntityFrameworkCore.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowInitialBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "automation");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:timescaledb", ",,");

            migrationBuilder.CreateTable(
                name: "workflow_compilations",
                schema: "automation",
                columns: table => new
                {
                    workflow_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    dependency_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_valid = table.Column<bool>(type: "boolean", nullable: false),
                    diagnostics_json = table.Column<string>(type: "jsonb", nullable: false),
                    block_sources_json = table.Column<string>(type: "jsonb", nullable: false),
                    compiler_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    compiled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_compilations", x => new { x.workflow_version_id, x.definition_digest, x.dependency_hash });
                });

            migrationBuilder.CreateTable(
                name: "workflow_debug_blobs",
                schema: "automation",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    written_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_debug_blobs", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "workflow_restart_generations",
                schema: "automation",
                columns: table => new
                {
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued = table.Column<long>(type: "bigint", nullable: false),
                    started = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_restart_generations", x => x.workflow_id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_restart_waiters",
                schema: "automation",
                columns: table => new
                {
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    live_until_unix_ms = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_restart_waiters", x => new { x.workflow_id, x.generation });
                });

            migrationBuilder.CreateTable(
                name: "execution_node_log",
                schema: "automation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExecutionLogId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NodeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    InputsJson = table.Column<string>(type: "jsonb", nullable: true),
                    OutputsJson = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ErrorJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_node_log", x => new { x.StartedAt, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "workflow_debug_sessions",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_execution_log_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_activity_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_debug_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_debug_snapshots",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    debug_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<string>(type: "text", nullable: false),
                    captured_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_debug_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_workflow_debug_snapshots_workflow_debug_sessions_debug_sess~",
                        column: x => x.debug_session_id,
                        principalSchema: "automation",
                        principalTable: "workflow_debug_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_execution_log_entries",
                schema: "automation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionLogId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "text", nullable: false),
                    BlockTypeId = table.Column<string>(type: "text", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DataJson = table.Column<string>(type: "jsonb", nullable: true),
                    IterationIndex = table.Column<int>(type: "integer", nullable: true),
                    CallStackJson = table.Column<string>(type: "jsonb", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_execution_log_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_execution_logs",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    automation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    attempt_number = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    outputs_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_outputs_json = table.Column<string>(type: "jsonb", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_execution_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_executions_state",
                schema: "automation",
                columns: table => new
                {
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    current_block_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    variables_json = table.Column<string>(type: "jsonb", nullable: false),
                    wait_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    wait_for_event_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    owned_by_replica_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_transition_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    trigger_source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    pinned_versions_json = table.Column<string>(type: "jsonb", nullable: true),
                    parent_execution_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_executions_state", x => x.execution_id);
                    table.CheckConstraint("CK_workflow_executions_state_status", "status IN ('Pending','Running','Sleeping','Waiting','Completed','Failed','Cancelled','Suspended')");
                })
                .Annotation("Npgsql:StorageParameter:fillfactor", 70);

            migrationBuilder.CreateTable(
                name: "workflow_suspension_dependency_keys",
                schema: "automation",
                columns: table => new
                {
                    suspension_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dependency_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_suspension_dependency_keys", x => new { x.suspension_id, x.dependency_key });
                });

            migrationBuilder.CreateTable(
                name: "workflow_suspensions",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_log_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    block_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: false),
                    state_payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    suspended_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    timeout_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome_json = table.Column<string>(type: "text", nullable: true),
                    take_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_suspensions", x => x.id);
                    table.ForeignKey(
                        name: "FK_workflow_suspensions_workflow_execution_logs_execution_log_~",
                        column: x => x.execution_log_id,
                        principalSchema: "automation",
                        principalTable: "workflow_execution_logs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_versions",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    definition_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_parameters_json = table.Column<string>(type: "jsonb", nullable: true),
                    output_parameters_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    change_message = table.Column<string>(type: "text", nullable: true),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflows",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    host_metadata = table.Column<string>(type: "jsonb", nullable: true),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    draft_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    has_import_warnings = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    import_warnings_json = table.Column<string>(type: "jsonb", nullable: true),
                    execution_timeout_seconds = table.Column<int>(type: "integer", nullable: true),
                    retry_policy_json = table.Column<string>(type: "text", nullable: true),
                    checkpoint_policy_json = table.Column<string>(type: "text", nullable: true),
                    mode = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    max_concurrent_runs = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    max_queued_runs = table.Column<int>(type: "integer", nullable: false, defaultValue: 10),
                    overflow_behavior = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    wait_poll_interval_seconds = table.Column<int>(type: "integer", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflows", x => x.id);
                    table.ForeignKey(
                        name: "FK_workflows_workflow_versions_current_version_id",
                        column: x => x.current_version_id,
                        principalSchema: "automation",
                        principalTable: "workflow_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_workflows_workflow_versions_draft_version_id",
                        column: x => x.draft_version_id,
                        principalSchema: "automation",
                        principalTable: "workflow_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_execution_node_log_ExecutionLogId_SortOrder",
                schema: "automation",
                table: "execution_node_log",
                columns: new[] { "ExecutionLogId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_debug_sessions_owner_user_id",
                schema: "automation",
                table: "workflow_debug_sessions",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_debug_sessions_workflow_execution_log_id",
                schema: "automation",
                table: "workflow_debug_sessions",
                column: "workflow_execution_log_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_debug_sessions_workflow_id",
                schema: "automation",
                table: "workflow_debug_sessions",
                column: "workflow_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_debug_snapshots_debug_session_id",
                schema: "automation",
                table: "workflow_debug_snapshots",
                column: "debug_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_execution_log_entries_ExecutionLogId_Timestamp",
                schema: "automation",
                table: "workflow_execution_log_entries",
                columns: new[] { "ExecutionLogId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_execution_logs_automation_id_started_at_utc",
                schema: "automation",
                table: "workflow_execution_logs",
                columns: new[] { "automation_id", "started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_execution_logs_trigger_id",
                schema: "automation",
                table: "workflow_execution_logs",
                column: "trigger_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_execution_logs_workflow_id",
                schema: "automation",
                table: "workflow_execution_logs",
                column: "workflow_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_execution_logs_workflow_version_id",
                schema: "automation",
                table: "workflow_execution_logs",
                column: "workflow_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_executions_state_event_key",
                schema: "automation",
                table: "workflow_executions_state",
                column: "wait_for_event_key",
                filter: "\"wait_for_event_key\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_executions_state_status_wait_until",
                schema: "automation",
                table: "workflow_executions_state",
                columns: new[] { "status", "wait_until_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_executions_state_workflow_started",
                schema: "automation",
                table: "workflow_executions_state",
                columns: new[] { "workflow_id", "started_at_utc", "execution_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_executions_state_workflow_status",
                schema: "automation",
                table: "workflow_executions_state",
                columns: new[] { "workflow_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_suspension_dependency_keys_key",
                schema: "automation",
                table: "workflow_suspension_dependency_keys",
                columns: new[] { "dependency_key", "suspension_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_suspensions_execution_log_id",
                schema: "automation",
                table: "workflow_suspensions",
                column: "execution_log_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_suspensions_parked_speculative",
                schema: "automation",
                table: "workflow_suspensions",
                columns: new[] { "suspended_at_utc", "id" },
                filter: "\"status\" = 'Suspended' AND \"kind\" IN (0, 1, 3, 4)");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_suspensions_status_timeout",
                schema: "automation",
                table: "workflow_suspensions",
                columns: new[] { "status", "timeout_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_suspensions_taken",
                schema: "automation",
                table: "workflow_suspensions",
                columns: new[] { "resolved_at_utc", "id" },
                filter: "\"status\" IN ('Resumed', 'TimedOut')");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_suspensions_workflow_id",
                schema: "automation",
                table: "workflow_suspensions",
                column: "workflow_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_created_by_user_id",
                schema: "automation",
                table: "workflow_versions",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_workflow_id_version_number",
                schema: "automation",
                table: "workflow_versions",
                columns: new[] { "workflow_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflows_created_by_user_id",
                schema: "automation",
                table: "workflows",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflows_current_version_id",
                schema: "automation",
                table: "workflows",
                column: "current_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflows_draft_version_id",
                schema: "automation",
                table: "workflows",
                column: "draft_version_id");

            migrationBuilder.AddForeignKey(
                name: "FK_execution_node_log_workflow_execution_logs_ExecutionLogId",
                schema: "automation",
                table: "execution_node_log",
                column: "ExecutionLogId",
                principalSchema: "automation",
                principalTable: "workflow_execution_logs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_debug_sessions_workflow_execution_logs_workflow_ex~",
                schema: "automation",
                table: "workflow_debug_sessions",
                column: "workflow_execution_log_id",
                principalSchema: "automation",
                principalTable: "workflow_execution_logs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_debug_sessions_workflows_workflow_id",
                schema: "automation",
                table: "workflow_debug_sessions",
                column: "workflow_id",
                principalSchema: "automation",
                principalTable: "workflows",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_execution_log_entries_workflow_execution_logs_Exec~",
                schema: "automation",
                table: "workflow_execution_log_entries",
                column: "ExecutionLogId",
                principalSchema: "automation",
                principalTable: "workflow_execution_logs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_execution_logs_workflow_versions_workflow_version_~",
                schema: "automation",
                table: "workflow_execution_logs",
                column: "workflow_version_id",
                principalSchema: "automation",
                principalTable: "workflow_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_execution_logs_workflows_workflow_id",
                schema: "automation",
                table: "workflow_execution_logs",
                column: "workflow_id",
                principalSchema: "automation",
                principalTable: "workflows",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_executions_state_workflows_workflow_id",
                schema: "automation",
                table: "workflow_executions_state",
                column: "workflow_id",
                principalSchema: "automation",
                principalTable: "workflows",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_suspension_dependency_keys_workflow_suspensions_su~",
                schema: "automation",
                table: "workflow_suspension_dependency_keys",
                column: "suspension_id",
                principalSchema: "automation",
                principalTable: "workflow_suspensions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_suspensions_workflows_workflow_id",
                schema: "automation",
                table: "workflow_suspensions",
                column: "workflow_id",
                principalSchema: "automation",
                principalTable: "workflows",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_versions_workflows_workflow_id",
                schema: "automation",
                table: "workflow_versions",
                column: "workflow_id",
                principalSchema: "automation",
                principalTable: "workflows",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.CreateHypertable(
                tableName: "execution_node_log",
                timeColumnName: "StartedAt",
                schema: "automation",
                chunkTimeInterval: "7 days",
                migrateData: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_workflow_versions_workflows_workflow_id",
                schema: "automation",
                table: "workflow_versions");

            migrationBuilder.DropTable(
                name: "execution_node_log",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_compilations",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_debug_blobs",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_debug_snapshots",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_execution_log_entries",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_executions_state",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_restart_generations",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_restart_waiters",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_suspension_dependency_keys",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_debug_sessions",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_suspensions",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_execution_logs",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflows",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_versions",
                schema: "automation");
        }
    }
}
