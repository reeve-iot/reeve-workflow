using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class WorkflowTypeReferenceTests
{
    private const string Why = "a record references only library types, host types and types of its own owner.";

    private static WorkflowTypeRecord Atom(string name) => new(name, typeof(string));
    private static WorkflowTypeConstructorRecord Ctor(string name, params string[] args) =>
        new(name, typeof(string)) { AllowedArguments = args };

    private static WorkflowTypeRegistry WithGadgetOfB()
    {
        var registry = new WorkflowTypeRegistry();
        registry.Register("plugin:b", [Atom("Gadget")]).Accepted.Should().BeTrue();
        return registry;
    }

    [Fact]
    public void An_allowed_argument_owned_by_another_plugin_refuses_the_constructor_with_the_message_the_specification_states()
    {
        var registry = WithGadgetOfB();

        var result = registry.Register("plugin:a", [Ctor("Box", "Gadget")]);

        result.Accepted.Should().BeFalse();
        result.Refusals.Should().ContainSingle().Which.Should().Be(new WorkflowRegistrationRefusal(
            "port-type-registration-invalid", "Box",
            $"Port type 'Box' cannot be registered: its allowed argument 'Gadget' is owned by plugin:b; {Why}"));
        (registry.Generation, result.Generation).Should().Be((1L, 1L));
    }

    [Fact]
    public void A_widening_row_owned_by_another_plugin_refuses_the_atom()
    {
        var registry = WithGadgetOfB();

        var result = registry.Register("plugin:a", [Atom("Widget") with { WidensTo = ["Gadget"] }]);

        result.Refusals.Should().ContainSingle().Which.Should().Be(new WorkflowRegistrationRefusal(
            "port-type-registration-invalid", "Widget",
            $"Port type 'Widget' cannot be registered: widening row 'Gadget' is owned by plugin:b; {Why}"));
    }

    [Fact]
    public void A_term_over_another_plugins_type_is_refused_at_any_depth()
    {
        var registry = WithGadgetOfB();

        registry.Register("plugin:a", [Ctor("Box", "Collection<Gadget>")]).Refusals.Single().Message.Should().Contain("owned by plugin:b");
    }

    [Fact]
    public void A_host_record_naming_a_plugins_type_is_refused_at_startup_because_the_type_is_not_registered_yet()
    {
        var building = () => new WorkflowTypeRegistry([Ctor("Box", "Gadget")]);

        building.Should().Throw<WorkflowTypeRegistrationException>().Which.Refusals.Should().ContainSingle()
            .Which.Code.Should().Be("port-type-registration-invalid");
    }

    [Fact]
    public void A_plugin_may_name_library_types_host_types_and_its_own_in_this_call_or_an_earlier_one()
    {
        var registry = new WorkflowTypeRegistry([Atom("Depot")]);

        registry.Register("plugin:a", [Ctor("Box", "Reading", "Depot", "Int", "Collection<Int>"), Atom("Reading")]).Accepted.Should().BeTrue();
        registry.Register("plugin:a", [Atom("Dial") with { WidensTo = ["Reading"] }, Ctor("Cell", "Box<Reading>")]).Accepted.Should().BeTrue();
    }

    [Fact]
    public void An_owner_leaves_with_every_record_so_no_reference_outlives_the_type_it_names()
    {
        var registry = new WorkflowTypeRegistry();
        registry.Register("plugin:a", [Atom("Reading"), Ctor("Box", "Reading"), Atom("Dial") with { WidensTo = ["Reading"] }]).Accepted.Should().BeTrue();

        registry.Unregister("plugin:a").Should().BeTrue();

        registry.Current.Types.Select(t => t.Name).Should().NotContain(["Reading", "Dial"]);
        registry.Current.Constructors.Select(c => c.Name).Should().Equal("Collection", "Map");
    }
}
