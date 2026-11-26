using FluentAssertions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CompilationOptionsTests
{
    [Fact]
    public void Two_namespace_registrations_claiming_the_same_namespace_are_refused()
    {
        var build = () => new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new Stub("device")), HostNamespaceRegistration.For(new Stub("device"))] };

        build.Should().Throw<ArgumentException>()
            .WithMessage("*device*")
            .Where(e => e.ParamName == "value");
    }

    [Fact]
    public void Namespace_registrations_differing_only_by_case_are_two_distinct_namespaces_not_a_collision()
    {
        var build = () => new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new Stub("device")), HostNamespaceRegistration.For(new Stub("Device"))] };

        build.Should().NotThrow();
    }

    [Fact]
    public void Distinct_namespace_registrations_are_unaffected()
    {
        var build = () => new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new Stub("device")), HostNamespaceRegistration.For(new Stub("scene"))] };

        build.Should().NotThrow();
    }

    private sealed class Stub(string name) : IExpressionNamespaceStaticCheck
    {
        public string Namespace => name;

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "String");
    }
}
