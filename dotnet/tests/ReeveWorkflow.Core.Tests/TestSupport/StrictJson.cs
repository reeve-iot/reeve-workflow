using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ReeveWorkflow.Core.Tests;

internal static class StrictJson
{
    public static JsonSerializerOptions Options { get; } = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver(), AllowDuplicateProperties = false };

    public static JsonTypeInfo Info(Type type) => Options.GetTypeInfo(type);
}
