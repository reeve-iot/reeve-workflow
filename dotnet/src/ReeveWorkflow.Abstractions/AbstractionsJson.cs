using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ReeveWorkflow.Abstractions;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(long?))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(double?))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateTimeOffset?))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(object[]))]
internal sealed partial class AbstractionsJsonContext : JsonSerializerContext;

internal static class AbstractionsJson
{
    private const string FallbackGuard =
        "Reached only when JsonSerializer.IsReflectionEnabledByDefault holds, which trimmed and NativeAOT publishes turn off by default.";

    internal static JsonTypeInfo<object?> AnyValue { get; } = (JsonTypeInfo<object?>)CreateAnyValueOptions().GetTypeInfo(typeof(object));

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = FallbackGuard)]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = FallbackGuard)]
    private static JsonSerializerOptions CreateAnyValueOptions()
    {
        var options = new JsonSerializerOptions();
        options.TypeInfoResolverChain.Add(AbstractionsJsonContext.Default);
        if (JsonSerializer.IsReflectionEnabledByDefault)
            options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        options.MakeReadOnly();
        return options;
    }
}
