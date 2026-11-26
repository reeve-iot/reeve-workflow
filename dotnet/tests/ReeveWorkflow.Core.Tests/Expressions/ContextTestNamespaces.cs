using System.Text.Json;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Tests.Expressions;

internal static class ContextTestNamespaces
{
    internal sealed class InferredCheck(IExpressionNamespaceResolver resolver) : IExpressionNamespaceStaticCheck
    {
        public string Namespace => resolver.Namespace;

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments)
        {
            var answered = resolver.Resolve([.. segments]);
            var term = !answered.Found ? "Json?" : answered.Value switch
            {
                long => "Int",
                double => "Number",
                bool => "Boolean",
                string => "String",
                DateTimeOffset => "DateTime",
                JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "Json?",
                JsonElement => "Json",
                _ => "Json?",
            };
            return new(NamespacePathCheck.Yes, term);
        }
    }
}
