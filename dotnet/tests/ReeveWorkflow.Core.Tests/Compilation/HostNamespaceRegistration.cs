using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Tests.Compilation;

internal static class HostNamespaceRegistration
{
    public static WorkflowExpressionNamespace For(IExpressionNamespaceStaticCheck check, int keySegments = 1) =>
        new(check.Namespace, keySegments, check,
            (_, _) => Task.FromResult<IExpressionNamespaceResolver>(new EmptyResolver(check.Namespace)));

    private sealed record EmptyResolver(string Namespace) : IExpressionNamespaceResolver
    {
        public ResolveResult Resolve(string[] segments) => ResolveResult.Failed();
    }
}
