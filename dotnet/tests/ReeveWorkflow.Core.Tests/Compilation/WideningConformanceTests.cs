using System.Text.RegularExpressions;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed partial class WideningConformanceTests
{
    private static readonly IReadOnlyList<EditorDefinition> Editor = ReadEditorBuiltins();

    [Fact]
    public void The_editor_builtins_are_read_whole() =>
        Editor.Should().HaveCount(NameEntry().Matches(BuiltinsText()).Count)
            .And.Contain(d => d.WidensTo.Count > 0);

    [Fact]
    public void Every_widening_the_editor_seeds_is_one_the_backend_enforces()
    {
        foreach (var definition in Editor)
            foreach (var target in definition.WidensTo)
            {
                PortTypeWideningTable.Builtin.IsAssignable(PortValueType.Of(definition.Name), PortValueType.Of(target))
                    .Should().BeTrue($"the editor seeds {definition.Name} widensTo {target}");
            }
    }

    [Fact]
    public void Every_name_either_table_knows_widens_to_exactly_what_the_editor_seeds() =>
        EveryName().Select(n => $"{n} -> {string.Join(",", Sorted(PortTypeWideningTable.Builtin.WideningsOf(PortValueType.Of(n)).Select(t => t.Name)))}")
            .Should().Equal(EveryName().Select(n => $"{n} -> {string.Join(",", Sorted(EditorEntry(n)?.WidensTo ?? []))}"));

    [Fact]
    public void Every_name_either_table_knows_has_exactly_the_aliases_the_editor_seeds() =>
        EveryName().Select(n => $"{n}: {string.Join(",", Sorted(PortTypeWideningTable.Builtin.AliasesOf(PortValueType.Of(n)).Select(t => t.Name)))}")
            .Should().Equal(EveryName().Select(n => $"{n}: {string.Join(",", Sorted(EditorEntry(n)?.Aliases ?? []))}"));

    [Fact]
    public void The_backend_knows_exactly_the_names_the_editor_seeds() =>
        Sorted(PortTypeWideningTable.Builtin.KnownTypes.Select(t => t.Name))
            .Should().Equal(Sorted(Editor.Select(d => d.Name)));

    private static List<string> EveryName() =>
        Sorted(PortTypeWideningTable.Builtin.KnownTypes.Select(t => t.Name).Concat(Editor.Select(d => d.Name)).Distinct());

    private static EditorDefinition? EditorEntry(string name) => Editor.SingleOrDefault(d => d.Name == name);

    private static List<string> Sorted(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal)];

    private sealed record EditorDefinition(string Name, IReadOnlyList<string> Aliases, IReadOnlyList<string> WidensTo);

    private static IReadOnlyList<EditorDefinition> ReadEditorBuiltins() =>
        [.. Definition().Matches(BuiltinsText()).Select(m => new EditorDefinition(
            m.Groups["name"].Value, List(m.Groups["aliases"].Value), List(m.Groups["widensTo"].Value)))];

    private static List<string> List(string items) =>
        [.. Quoted().Matches(items).Select(m => m.Groups[1].Value)];

    private static string BuiltinsText()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "packages", "core", "src", "portTypes", "builtins.ts");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("packages/core/src/portTypes/builtins.ts was not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\{\s*name:\s*'(?<name>[^']+)'(?:,\s*aliases:\s*\[(?<aliases>[^\]]*)\])?(?:,\s*widensTo:\s*\[(?<widensTo>[^\]]*)\])?(?:,\s*collection:\s*false)?\s*\}")]
    private static partial Regex Definition();

    [GeneratedRegex(@"\bname:")]
    private static partial Regex NameEntry();

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex Quoted();
}
