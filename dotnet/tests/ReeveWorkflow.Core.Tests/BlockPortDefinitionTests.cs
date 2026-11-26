using FluentAssertions;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Tests;

public class BlockPortDefinitionTests
{
    [Fact]
    public void The_five_argument_constructor_builds_a_port_with_its_allowed_values()
    {
        var port = new BlockPortDefinition("X", "X", PortValueType.String, true, AllowedValues: ["a", "b"]);

        port.Type.Should().Be(PortValueType.String);
        port.AllowedValues.Should().Equal("a", "b");
    }

    [Fact]
    public void A_generic_port_declares_its_term_with_the_blocks_type_parameter()
    {
        var port = new BlockPortDefinition("Results", "Results", PortValueType.OfDeclaration("Collection<$R>"), false);

        port.Type.Name.Should().Be("Collection<$R>");
    }

    [Fact]
    public void A_default_type_through_with_still_throws()
    {
        var port = new BlockPortDefinition("X", "X", PortValueType.String, true);
        var act = () => port with { Type = default };

        act.Should().Throw<ArgumentException>();
    }
}
