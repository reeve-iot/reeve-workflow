using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class RefusalCodesContractTests
{
    private sealed record Row(string Code, string Kind, string RaisedBy);

    private static List<Row> Rows()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.RefusalCodesPath()));
        return [.. document.RootElement.GetProperty("rows").EnumerateArray().Select(row => new Row(
            row.GetProperty("code").GetString()!, row.GetProperty("kind").GetString()!, row.GetProperty("raisedBy").GetString()!))];
    }

    private static IReadOnlyList<Assembly> LibraryAssemblies { get; } =
        [typeof(WorkflowErrorCodes).Assembly, typeof(WorkflowGraphExecutor).Assembly];

    private static Type ClassNamed(string name)
    {
        var matches = LibraryAssemblies.SelectMany(a => a.GetExportedTypes())
            .Where(t => t.Name == name && t.IsAbstract && t.IsSealed).ToList();
        matches.Should().ContainSingle($"'{name}' is a public static class of the library");
        return matches[0];
    }

    private static List<string> Constants(Type type) => [.. type
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)];

    [Fact]
    public void Codes_are_unique_and_every_row_names_a_kind_and_a_class()
    {
        var rows = Rows();
        rows.Should().NotBeEmpty();
        rows.Select(r => r.Code).Should().OnlyHaveUniqueItems();
        rows.Should().OnlyContain(r => r.Kind.Length > 0 && r.RaisedBy.Length > 0 && r.Code.Trim() == r.Code && r.Code.Length > 0);
    }

    [Fact]
    public void Every_constant_of_each_named_class_is_a_row_and_every_row_is_a_constant()
    {
        foreach (var group in Rows().GroupBy(r => r.RaisedBy))
        {
            var constants = Constants(ClassNamed(group.Key));
            var codes = group.Select(r => r.Code).ToList();
            if (group.Key is "WorkflowAuditEventKinds")
                constants.Should().Contain(codes, $"each row of {group.Key} is a constant");
            else
                constants.Should().BeEquivalentTo(codes, $"{group.Key}'s constants and its rows are the same set");
        }
    }

    [Fact]
    public void The_error_codes_are_the_ones_the_library_raises_so_far()
    {
        Rows().Where(r => r.Kind == "_error.code").Select(r => r.Code).Should().BeEquivalentTo(
            ["block-failed", "block-output-type-mismatch", "config_value_not_allowed", "expression-runtime-error", "json-projection-failed",
             "required-input-value-absent", "secret-unresolved", "suspend-config-invalid"]);
    }

    [Fact]
    public void The_library_exceptions_that_name_a_code_name_a_row()
    {
        var codes = Rows().Select(r => r.Code).ToList();
        new WorkflowConfigValueNotAllowedException("n", "Op", ["Add"], "x").Code.Should().Be("config_value_not_allowed").And.BeOneOf(codes);
        new WorkflowRequiredInputAbsentException("n", "A").Code.Should().Be("required-input-value-absent").And.BeOneOf(codes);
        new WorkflowSuspendConfigInvalidException("n", "r").Code.Should().Be("suspend-config-invalid").And.BeOneOf(codes);
        new Core.Expressions.ExpressionEvaluationException("m", 0).Code.Should().Be("expression-runtime-error").And.BeOneOf(codes);
        new Core.Compilation.JsonProjectionException("m", Core.Compilation.JsonProjectionException.Narrowing).Code
            .Should().Be("json-projection-failed").And.BeOneOf(codes);
    }

    [Fact]
    public void A_host_finding_may_not_carry_any_code_the_contract_lists()
    {
        foreach (var row in Rows())
        {
            FluentActions.Invoking(() => new Core.Hosting.WorkflowValidationFinding(row.Code, "x"))
                .Should().Throw<ArgumentException>($"'{row.Code}' is a library code");
        }

        foreach (var code in new[] { "satisfied", "timed_out", "enum-field-no-values" })
        {
            FluentActions.Invoking(() => new Core.Hosting.WorkflowValidationFinding(code, "x"))
                .Should().Throw<ArgumentException>($"'{code}' is a library code");
        }
    }
}
