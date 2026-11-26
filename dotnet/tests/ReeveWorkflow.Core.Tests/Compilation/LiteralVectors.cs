using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

internal static class LiteralVectors
{
    public static WorkflowLiteralForm? FormOf(JsonElement form) => form.ValueKind == JsonValueKind.Null
        ? null
        : new WorkflowLiteralForm(
            Enum.Parse<WorkflowLiteralKind>(form.GetProperty("gradedAs").GetString()!),
            form.TryGetProperty("format", out var format) ? Enum.Parse<WorkflowLiteralFormat>(format.GetString()!) : null);

    private static (Type Clr, System.Text.Json.Serialization.Metadata.JsonTypeInfo Info) ReaderOf(WorkflowLiteralForm? form)
    {
        var clr = form switch
        {
            { GradedAs: WorkflowLiteralKind.Json } => typeof(JsonElement),
            { GradedAs: WorkflowLiteralKind.Boolean } => typeof(bool),
            { GradedAs: WorkflowLiteralKind.Int } => typeof(long),
            { GradedAs: WorkflowLiteralKind.Number } => typeof(double),
            { Format: WorkflowLiteralFormat.Uuid } => typeof(Guid),
            _ => typeof(string),
        };
        return (clr, StrictJson.Info(clr));
    }

    public static IReadOnlyList<WorkflowTypeRegistrationRecord> RecordsOf(JsonElement file)
    {
        var records = new List<WorkflowTypeRegistrationRecord>();
        foreach (var atom in file.GetProperty("hostAtoms").EnumerateObject())
        {
            var form = FormOf(atom.Value);
            var (clr, info) = ReaderOf(form);
            records.Add(new WorkflowTypeRecord(atom.Name, clr) { Literal = form, JsonTypeInfo = info });
        }

        foreach (var constructor in file.GetProperty("hostConstructors").EnumerateObject())
        {
            var form = FormOf(constructor.Value.GetProperty("literal"));
            var (clr, info) = ReaderOf(form);
            records.Add(new WorkflowTypeConstructorRecord(constructor.Name, clr)
            {
                AllowedArguments = [.. constructor.Value.GetProperty("allowedArguments").EnumerateArray().Select(a => a.GetString()!)],
                Literal = form,
                JsonTypeInfo = info,
            });
        }

        return records;
    }

    public static bool StoredAsWritten(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => StoredNumber.Refusal(value.GetRawText()) is null,
        JsonValueKind.Array => value.EnumerateArray().All(StoredAsWritten),
        JsonValueKind.Object => value.EnumerateObject().All(p => StoredAsWritten(p.Value)),
        _ => true,
    };

    public static WorkflowTypeVocabulary VocabularyOf(JsonElement file) => new WorkflowTypeRegistry(RecordsOf(file)).Current;

    public static CompilationOptions OptionsOf(JsonElement file) => new() { TypeRegistry = new WorkflowTypeRegistry(RecordsOf(file)) };
}
