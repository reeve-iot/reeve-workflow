using System.Globalization;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests;

public class PortTypeNameCorpusTests
{
    private static readonly IReadOnlyList<CorpusRow> Rows = Load();

    [Fact]
    public void The_corpus_has_more_than_9000_names()
    {
        Rows.Should().HaveCountGreaterThan(9000);
    }

    [Fact]
    public void Every_well_formed_row_parses_to_its_pinned_term()
    {
        foreach (var row in Rows.Where(r => r.Kind == "term"))
        {
            PortValueType.TryParse(row.Name, out var type, out var error).Should().BeTrue(
                because: $"{Describe(row.Name)} is pinned well-formed at depth {row.Depth}");
            error.Should().BeNull();
            AssertTerm(row, type);
        }
    }

    [Fact]
    public void Every_well_formed_row_canonicalises_under_Builtin_to_its_pinned_name()
    {
        foreach (var row in Rows.Where(r => r.Kind == "term"))
        {
            ReeveWorkflow.Core.Compilation.PortTypeWideningTable.Builtin.Canonicalize(PortValueType.Of(row.Name)).Name
                .Should().Be(row.Canonical, because: Describe(row.Name));
        }
    }

    [Fact]
    public void Every_malformed_row_is_refused_with_its_pinned_fault_and_position()
    {
        foreach (var row in Rows.Where(r => r.Kind == "syntax"))
        {
            PortValueType.TryParse(row.Name, out _, out var error).Should().BeFalse(
                because: $"{Describe(row.Name)} is pinned malformed with {row.Fault}");
            error!.Fault.Should().Be(row.Fault, because: Describe(row.Name));
            error.Position.Should().Be(row.Position, because: Describe(row.Name));
        }
    }

    [Fact]
    public void The_nullable_and_constructor_section_is_present()
    {
        Rows.Count(r => r.Kind == "doc").Should().BeGreaterThan(40);
        Rows.Count(r => r.Kind is "decl").Should().BeGreaterThan(10);
        Rows.Count(r => r.Kind is "refused" or "declrefused").Should().BeGreaterThan(1000);
    }

    [Fact]
    public void The_reserved_section_is_present_and_names_both_reserved_names()
    {
        Rows.Where(r => r.Kind == "reserved").Should().HaveCountGreaterThan(100);
        Rows.Where(r => r.Kind == "reserved").Select(r => r.Shape).Distinct().Should().BeEquivalentTo(["Any", "Float", "-"]);
    }

    [Fact]
    public void Every_reserved_row_finds_the_reserved_name_it_pins_outermost_first_in_any_case_and_at_any_depth()
    {
        foreach (var row in Rows.Where(r => r.Kind == "reserved"))
        {
            PortTerm.TryParse(row.Name, PortTermGrammar.Document, out _, out var error).Should().BeTrue(
                because: $"{Describe(row.Name)} is well-formed: {error?.Message}");
            (ReeveWorkflow.Core.Compilation.PortTypeNameSites.ReservedName(row.Name) ?? "-").Should().Be(row.Shape, because: Describe(row.Name));
        }
    }

    [Fact]
    public void Every_shape_row_parses_to_its_shape_and_renders_as_it_was_written()
    {
        foreach (var row in Rows.Where(r => r.Kind is "doc" or "decl"))
        {
            var declaration = PortTerm.TryParse(row.Name, PortTermGrammar.Declaration, out var term, out var error);
            declaration.Should().BeTrue(because: $"{Describe(row.Name)} is pinned well-formed under the declaration grammar: {error?.Message}");
            Shape(term!).Should().Be(row.Shape, because: Describe(row.Name));
            term!.ToString().Should().Be(row.Name, because: Describe(row.Name));

            var document = PortTerm.TryParse(row.Name, PortTermGrammar.Document, out var documentTerm, out var documentError);
            if (row.Kind == "doc")
            {
                document.Should().BeTrue(because: Describe(row.Name));
                Shape(documentTerm!).Should().Be(row.Shape, because: Describe(row.Name));
                PortValueType.TryParse(row.Name, out var stored, out _).Should().BeTrue(because: Describe(row.Name));
                stored.Name.Should().Be(row.Name, because: Describe(row.Name));
            }
            else
            {
                document.Should().BeFalse(because: Describe(row.Name));
                documentError!.Fault.Should().Be(row.DocFault, because: Describe(row.Name));
                documentError.Position.Should().Be(row.DocPosition, because: Describe(row.Name));
                PortValueType.TryParse(row.Name, out _, out _).Should().BeFalse(because: Describe(row.Name));
            }
        }
    }

    [Fact]
    public void Every_refused_row_is_refused_with_its_pinned_fault_and_position_under_each_grammar()
    {
        foreach (var row in Rows.Where(r => r.Kind is "refused" or "declrefused"))
        {
            PortTerm.TryParse(row.Name, PortTermGrammar.Declaration, out _, out var declaration).Should().BeFalse(because: Describe(row.Name));
            declaration!.Fault.Should().Be(row.Fault, because: Describe(row.Name));
            declaration.Position.Should().Be(row.Position, because: Describe(row.Name));

            PortTerm.TryParse(row.Name, PortTermGrammar.Document, out _, out var document).Should().BeFalse(because: Describe(row.Name));
            document!.Fault.Should().Be(row.DocFault, because: Describe(row.Name));
            document.Position.Should().Be(row.DocPosition, because: Describe(row.Name));
        }
    }

    [Fact]
    public void Every_older_row_reads_the_same_under_the_declaration_grammar_a_dollar_aside()
    {
        foreach (var row in Rows.Where(r => r.Kind is "term" or "syntax" && !r.Name.Contains('$')))
        {
            var document = PortTerm.TryParse(row.Name, PortTermGrammar.Document, out var documentTerm, out var documentError);
            var declaration = PortTerm.TryParse(row.Name, PortTermGrammar.Declaration, out var declarationTerm, out var declarationError);

            declaration.Should().Be(document, because: Describe(row.Name));
            if (document)
                declarationTerm.Should().Be(documentTerm, because: Describe(row.Name));
            else
                (declarationError!.Fault, declarationError.Position).Should().Be((documentError!.Fault, documentError.Position), because: Describe(row.Name));
        }
    }

    private static string Shape(PortTerm term)
    {
        var prefix = new System.Text.StringBuilder();
        var suffix = new System.Text.StringBuilder();
        for (var node = term; node is not null; node = node.LastArgument)
        {
            if (node.IsNullable) prefix.Append('?');
            prefix.Append(node.Kind == PortTermKind.Parameter ? "$" : "").Append(node.Name);
            if (node.Arguments.Count > 0)
            {
                prefix.Append('(');
                foreach (var key in node.Arguments.SkipLast(1)) prefix.Append(key.Name).Append(',');
                suffix.Append(')');
            }
        }
        return prefix.Append(suffix).ToString();
    }

    private static void AssertTerm(CorpusRow row, PortValueType type)
    {
        var unwrapped = type;
        for (var i = 0; i < row.Depth; i++)
        {
            unwrapped.IsCollection.Should().BeTrue(because: Describe(row.Name));
            unwrapped = unwrapped.ElementType;
        }
        unwrapped.IsCollection.Should().BeFalse(because: Describe(row.Name));
        unwrapped.Name.Should().Be(row.Element, because: Describe(row.Name));
    }

    private static string Describe(string? name) => $"name '{name}'";

    private sealed record CorpusRow(
        string Name, string Kind, int Depth, string? Element, string? Canonical,
        PortTypeSyntaxFault Fault, int Position,
        string Shape = "", PortTypeSyntaxFault DocFault = default, int DocPosition = -1);

    private static IReadOnlyList<CorpusRow> Load()
    {
        var rows = new List<CorpusRow>();
        foreach (var line in File.ReadLines(RepositoryFiles.PortTypeNameCorpusPath()))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var fields = line.Split('\t');
            var name = RepositoryFiles.UnescapeCorpusField(fields[0]);
            var kind = fields[1].StartsWith("map:", StringComparison.Ordinal) ? fields[1]["map:".Length..] : fields[1];
            switch (kind)
            {
                case "term":
                {
                    var depth = int.Parse(fields[2], CultureInfo.InvariantCulture);
                    rows.Add(new CorpusRow(name, kind, depth, fields[3], fields[4], default, -1));
                    break;
                }
                case "syntax":
                    rows.Add(new CorpusRow(name, kind, 0, null, null, Enum.Parse<PortTypeSyntaxFault>(fields[2]),
                        int.Parse(fields[3], CultureInfo.InvariantCulture)));
                    break;
                case "doc":
                    rows.Add(new CorpusRow(name, kind, 0, null, null, default, -1, fields[2]));
                    break;
                case "reserved":
                    rows.Add(new CorpusRow(name, kind, 0, null, null, default, -1, fields[2]));
                    break;
                case "decl":
                    rows.Add(new CorpusRow(name, kind, 0, null, null, default, -1, fields[2],
                        Enum.Parse<PortTypeSyntaxFault>(fields[3]), int.Parse(fields[4], CultureInfo.InvariantCulture)));
                    break;
                case "refused":
                {
                    var fault = Enum.Parse<PortTypeSyntaxFault>(fields[2]);
                    var position = int.Parse(fields[3], CultureInfo.InvariantCulture);
                    rows.Add(new CorpusRow(name, kind, 0, null, null, fault, position, "", fault, position));
                    break;
                }
                case "declrefused":
                    rows.Add(new CorpusRow(name, kind, 0, null, null, Enum.Parse<PortTypeSyntaxFault>(fields[2]),
                        int.Parse(fields[3], CultureInfo.InvariantCulture), "",
                        Enum.Parse<PortTypeSyntaxFault>(fields[4]), int.Parse(fields[5], CultureInfo.InvariantCulture)));
                    break;
                default:
                    throw new InvalidDataException($"Unknown corpus row kind '{kind}': {line}");
            }
        }
        return rows;
    }

}
