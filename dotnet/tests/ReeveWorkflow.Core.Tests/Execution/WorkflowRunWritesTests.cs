using FluentAssertions;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunWritesTests
{
    [Fact]
    public void A_write_that_carries_no_checkpoint_leaves_the_row_naming_no_entry()
    {
        var writes = WorkflowRunWrites.Begin(Guid.NewGuid(), recoverable: true, rowNamesEntry: true);

        writes.Wrote(DateTimeOffset.UtcNow, rootCheckpoint: null, namesEntry: false);

        writes.RowNamesEntry.Should().BeFalse("the write replaced the checkpoint that named the entry with bare variables");
    }

    [Fact]
    public void A_write_that_carries_a_checkpoint_names_the_entry_again()
    {
        var writes = WorkflowRunWrites.Begin(Guid.NewGuid(), recoverable: true);
        writes.Wrote(DateTimeOffset.UtcNow, rootCheckpoint: null, namesEntry: false);

        writes.Wrote(DateTimeOffset.UtcNow, rootCheckpoint: "{}", namesEntry: true);

        writes.RowNamesEntry.Should().BeTrue();
    }
}
