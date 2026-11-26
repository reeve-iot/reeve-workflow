using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

internal static class HostNamespaceRegistration
{
    public static WorkflowExpressionNamespace For(IExpressionNamespaceStaticCheck check) =>
        new(check.Namespace, 1, check,
            (_, _) => Task.FromResult<IExpressionNamespaceResolver>(new EmptyResolver(check.Namespace)));

    private sealed record EmptyResolver(string Namespace) : IExpressionNamespaceResolver
    {
        public ResolveResult Resolve(string[] segments) => ResolveResult.Failed();
    }
}
