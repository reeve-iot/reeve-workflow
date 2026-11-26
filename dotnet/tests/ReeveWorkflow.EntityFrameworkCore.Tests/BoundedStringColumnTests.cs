using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class BoundedStringColumnTests
{
    private static readonly Dictionary<string, (int Bound, string Why)> Contracts = new(StringComparer.Ordinal)
    {
        ["workflow_execution_logs.error_message"] =
            (2000, "cut to 2000 before it is written"),
        ["workflow_execution_logs.failure_reason"] =
            (64, "one of two constants the compilation gate writes"),
        ["workflow_execution_log_entries.Message"] =
            (4000, "cut to 4000 where it is written: admission cuts a host's own line, and AddLogEntry cuts every "
                + "line the library composes from one"),
        ["execution_node_log.NodeId"] =
            (WorkflowGraphHelpers.NodeLogTextLimit, "the node log is the table the Timescale add-on converts to a "
                + "compressed hypertable, which refuses the ALTER COLUMN TYPE a widening is — so the width cannot "
                + "move and WorkflowCompiler refuses a graph that does not fit it (node-id-too-long)"),
        ["execution_node_log.NodeType"] =
            (WorkflowGraphHelpers.NodeLogTextLimit, "the same table and the same width: the column holds a node's "
                + "block type id, and WorkflowCompiler refuses a block type whose own id does not fit it "
                + "(block-type-id-too-long)"),
        ["execution_node_log.ErrorMessage"] =
            (2000, "cut to 2000 before it is written"),
        ["execution_node_log.ErrorStackTrace"] =
            (8000, "cut to 8000 before it is written"),
        ["workflow_executions_state.status"] =
            (20, "the names of the run's status enum, which a CHECK holds closed"),
        ["workflow_executions_state.current_block_path"] =
            (500, "a node id, and the bound the suspension row's block_id carries too: one park writes both. Kept "
                + "to leave this table out of a patch release's migration, not because a replica on another "
                + "version could not read a wider column"),
        ["workflow_executions_state.wait_for_event_key"] =
            (200, "a token this library builds: reeveworkflow:parked:{guid}"),
        ["workflow_executions_state.owned_by_replica_id"] =
            (100, "the host's own replica id, refused where the host configures it rather than by a state write "
                + "failing once a run is under way"),
        ["workflow_executions_state.failure_reason"] =
            (2000, "cut to 400 by WorkflowRunEnding before it is written"),
        ["workflow_executions_state.trigger_source"] =
            (200, "a token this library builds: trigger:{guid}"),
        ["workflow_suspensions.block_id"] =
            (500, "a node id, and the bound the state row's current_block_path carries too: one park writes both, "
                + "so widening one alone would park a run whose state row still says Running, which recovery then "
                + "reclaims as abandoned"),
        ["workflow_suspensions.status"] =
            (20, "the names of the suspension status enum, and only this library's own store writes one"),
        ["workflow_debug_blobs.key"] =
            (200, "every key is built by this library, and a b-tree entry has a limit of its own"),
        ["workflow_compilations.definition_digest"] =
            (64, "a SHA-256 digest, in hex"),
        ["workflow_compilations.dependency_hash"] =
            (64, "a SHA-256 digest, in hex"),
        ["workflow_compilations.compiler_version"] =
            (100, "built by the compiler from its own version and a hash of it")
    };

    [Fact]
    public void Every_bounded_string_column_is_an_allowed_contract()
    {
        using var database = new SqliteWorkflowDatabase();
        using var scope = database.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Model;

        var bounded = model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Where(property => ProviderType(property) == typeof(string)
                    && property.GetMaxLength() is not null)
                .Select(property => new
                {
                    Column = $"{entity.GetTableName()}.{property.GetColumnName()}",
                    Bound = property.GetMaxLength()!.Value
                }))
            .ToDictionary(row => row.Column, row => row.Bound, StringComparer.Ordinal);

        bounded.Should().Equal(
            Contracts.ToDictionary(contract => contract.Key, contract => contract.Value.Bound, StringComparer.Ordinal),
            "a bound here that is none of these is a host's write failing on the day its data grows: widen the "
            + "column to text for the host data it carries, or add it above with the reason it is a contract");
    }

    private static Type ProviderType(IReadOnlyProperty property) =>
        property.GetTypeMapping().Converter?.ProviderClrType ?? property.ClrType;
}
