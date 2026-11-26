#pragma warning disable CA1861
using FluentAssertions;
using ReeveWorkflow.Core.Expressions;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class DependencyWalkerTests
{
    private readonly ExpressionDependencyWalker _walker = ExpressionDependencyWalker.Instance;

    [Fact]
    public void DependenciesFromSource_SingleDeviceDependency()
    {
        var deps = _walker.DependenciesFromSource("${device.kitchen.temperature} > 22");

        deps.Should().ContainSingle();
        deps.Should().Contain("device:kitchen:temperature");
    }

    [Fact]
    public void DependenciesFromSource_MultipleMixedDependencies()
    {
        var deps = _walker.DependenciesFromSource(
            "${helper.vacation_mode} || (${device.kitchen.temperature} > 22 && ${device.bedroom.humidity} < 50)");

        deps.Should().HaveCount(3);
        deps.Should().Contain("helper:vacation_mode");
        deps.Should().Contain("device:kitchen:temperature");
        deps.Should().Contain("device:bedroom:humidity");
    }

    [Fact]
    public void DependenciesFromSource_FunctionCallArguments()
    {
        var deps = _walker.DependenciesFromSource("max(${device.living_room.lux}, 100)");

        deps.Should().ContainSingle();
        deps.Should().Contain("device:living_room:lux");
    }

    [Fact]
    public void DependenciesFromSource_NestedFunctionCalls()
    {
        var deps = _walker.DependenciesFromSource(
            "min(${device.a.x}, max(${device.b.y}, ${device.c.z}))");

        deps.Should().HaveCount(3);
        deps.Should().Contain("device:a:x");
        deps.Should().Contain("device:b:y");
        deps.Should().Contain("device:c:z");
    }

    [Fact]
    public void DependenciesFromSource_TernaryExpression()
    {
        var deps = _walker.DependenciesFromSource(
            "${device.mode} == \"auto\" ? ${device.temp} : ${device.fallback}");

        deps.Should().HaveCount(3);
        deps.Should().Contain("device:mode");
        deps.Should().Contain("device:temp");
        deps.Should().Contain("device:fallback");
    }

    [Fact]
    public void DependenciesFromSource_UserProperty()
    {
        var deps = _walker.DependenciesFromSource("${user.alice.location}");

        deps.Should().ContainSingle();
        deps.Should().Contain("user:alice:location");
    }

    [Fact]
    public void DependenciesFromSource_HelperNestedPath()
    {
        var deps = _walker.DependenciesFromSource("${helper.kitchen_light.count_on} > 0");

        deps.Should().ContainSingle();
        deps.Should().Contain("helper:kitchen_light:count_on");
    }

    [Fact]
    public void DependenciesFromSource_UnaryOperator()
    {
        var deps = _walker.DependenciesFromSource("!${device.motion.detected}");

        deps.Should().ContainSingle();
        deps.Should().Contain("device:motion:detected");
    }

    [Fact]
    public void DependenciesFromSource_ComplexNestedExpression()
    {
        var deps = _walker.DependenciesFromSource(
            "!${device.armed} && (${helper.night_mode} || (${device.temp} > 20 ? max(${device.lux}, 100) : ${device.fallback_lux}))");

        deps.Should().HaveCount(5);
        deps.Should().Contain("device:armed");
        deps.Should().Contain("helper:night_mode");
        deps.Should().Contain("device:temp");
        deps.Should().Contain("device:lux");
        deps.Should().Contain("device:fallback_lux");
    }

    [Fact]
    public void DependenciesFromSource_DeduplicatesSameDependency()
    {
        var deps = _walker.DependenciesFromSource("${device.temp} + ${device.temp}");

        deps.Should().ContainSingle();
        deps.Should().Contain("device:temp");
    }

    [Fact]
    public void DependenciesFromSource_LiteralsOnly()
    {
        var deps = _walker.DependenciesFromSource("42 + 3.14 || true && \"hello\" ?? null");

        deps.Should().BeEmpty();
    }

    [Fact]
    public void DependenciesFromSource_EmptyInput()
    {
        _walker.DependenciesFromSource(null).Should().BeEmpty();
        _walker.DependenciesFromSource("").Should().BeEmpty();
        _walker.DependenciesFromSource("   ").Should().BeEmpty();
    }

    [Fact]
    public void DependenciesFromSource_WithExpressionPrefix()
    {
        var deps = _walker.DependenciesFromSource("expr:${device.temp}");

        deps.Should().ContainSingle();
        deps.Should().Contain("device:temp");
    }

    [Fact]
    public void DependenciesFromSource_SyntaxError()
    {
        var deps = _walker.DependenciesFromSource("${device.temp} >> << !! invalid @@");

        deps.Should().BeEmpty();
    }

    [Fact]
    public void DependenciesFromSource_DotToColonNormalisation()
    {
        var deps = _walker.DependenciesFromSource("${device.living.room.lux}");
        deps.Should().ContainSingle();
        deps.Should().Contain("device:living:room:lux");
    }

    private static ExpressionDependencyWalker Walker(params (string Name, int Key)[] hosts) =>
        new(ExpressionKeyShapes.Library.With(hosts.Select(h => (h.Name, h.Key))));

    [Theory]
    [InlineData("${previous.w.TriggerData.auth}", "previous:w:TriggerData")]
    [InlineData("${previous.w.Items[0].name}", "previous:w:Items")]
    [InlineData("${parameter.limits.max[2]}", "parameter:limits")]
    [InlineData("${now.year.x}", "now:year")]
    [InlineData("${now[0]}", "now")]
    [InlineData("${helper.KitchenLight.brightness}", "helper:KitchenLight:brightness")]
    public void A_field_path_is_not_part_of_the_dependency_key(string source, string expected) =>
        Walker().DependenciesFromSource(source).Should().ContainSingle().Which.Should().Be(expected);

    [Fact]
    public void A_host_namespace_key_ends_where_its_registration_says()
    {
        var walker = Walker(("device", 2), ("trigger", 1));

        walker.DependenciesFromSource("${device.kitchen.temp.unit} > 1 && ${trigger.payload.auth.user}").Should()
            .BeEquivalentTo(["device:kitchen:temp", "trigger:payload"]);
    }

    [Fact]
    public void A_registration_list_builds_the_walker_a_store_indexes_with()
    {
        var registration = new WorkflowExpressionNamespace("device", 2, new DeviceCheck(),
            (_, _) => throw new InvalidOperationException("not needed"));

        ExpressionDependencyWalker.For([registration]).DependenciesFromSource("${device.a.b.c}").Should().ContainSingle()
            .Which.Should().Be("device:a:b");
    }

    private sealed class DeviceCheck : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "device";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "String");
    }
}
