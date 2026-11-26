using ReeveWorkflow.Core.Expressions;

namespace Dispatch.Host.TestHooks;

public sealed class TestNamespaceStaticCheck : IExpressionNamespaceStaticCheck
{
    public string Namespace => "e2e";

    public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => segments switch
    {
        ["known"] => new(NamespacePathCheck.Yes, "Boolean"),
        ["document"] => new(NamespacePathCheck.Yes, "Json"),
        _ => NamespacePathCheckAnswer.No,
    };
}
