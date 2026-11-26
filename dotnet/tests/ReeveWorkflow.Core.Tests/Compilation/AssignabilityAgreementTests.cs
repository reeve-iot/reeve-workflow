using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class AssignabilityAgreementTests
{
    private static readonly JsonElement File = LoadFile();

    private static IReadOnlyList<string> Names => [.. File.GetProperty("names").EnumerateArray().Select(n => n.GetString()!)];

    [Fact]
    public void The_backend_lets_exactly_the_pairs_feed_each_other_the_editor_lets()
    {
        var names = Names;
        var rows = File.GetProperty("assignable").EnumerateArray().Select(r => r.GetString()!).ToList();
        rows.Should().HaveCount(names.Count).And.OnlyContain(r => r.Length == names.Count);
        rows.SelectMany(r => r).Should().Contain('0').And.Contain('1');

        var differ = new List<string>();
        for (var i = 0; i < names.Count; i++)
            for (var j = 0; j < names.Count; j++)
            {
                var backend = WorkflowTypeVocabulary.Builtin.IsAssignable(PortValueType.Of(names[i]), PortValueType.Of(names[j]));
                if (backend != (rows[i][j] == '1'))
                    differ.Add($"{names[i]} -> {names[j]}: backend {backend}");
            }

        differ.Should().BeEmpty();
    }

    [Fact]
    public void The_backend_canonicalises_each_name_as_the_editor_normalises_it()
    {
        var normalized = File.GetProperty("normalize").EnumerateArray().Select(n => n.GetString()!).ToList();
        Names.Select((name, i) => (name, editor: normalized[i], backend: PortTypeWideningTable.Builtin.Canonicalize(PortValueType.Of(name)).Name))
            .Where(r => !string.Equals(r.editor, r.backend, StringComparison.OrdinalIgnoreCase))
            .Select(r => $"{r.name}: editor {r.editor}, backend {r.backend}")
            .Should().BeEmpty();
    }

    private static JsonElement Terms => File.GetProperty("terms");

    private static readonly Dictionary<string, bool[]> Answers = new()
    {
        ["A1"] = [true], ["A2"] = [true, false], ["A3"] = [false], ["A4"] = [true, false],
        ["A5"] = [true, false], ["A6"] = [true], ["A7"] = [false],
    };

    [Fact]
    public void Every_rule_has_a_vector_for_each_answer_it_can_give_and_none_it_cannot()
    {
        var vectors = Terms.GetProperty("vectors").EnumerateArray()
            .Select(v => (Rule: v.GetProperty("rule").GetString()!, Answer: v.GetProperty("assignable").GetBoolean())).ToList();
        foreach (var (rule, answers) in Answers)
            foreach (var answer in answers)
                vectors.Count(v => v.Rule == rule && v.Answer == answer).Should().BeGreaterThan(0, because: $"{rule} {answer}");

        vectors.Where(v => !Answers.TryGetValue(v.Rule, out var allowed) || !allowed.Contains(v.Answer))
            .Select(v => $"{v.Rule}: {v.Answer}").Should().BeEmpty();
    }

    [Fact]
    public void The_compiler_answers_every_term_vector_as_the_specification_does()
    {
        var vocabulary = new VectorVocabulary(Terms.GetProperty("vocabulary"));
        var wrong = new List<string>();
        foreach (var vector in Terms.GetProperty("vectors").EnumerateArray())
        {
            var from = vector.GetProperty("from").ValueKind == JsonValueKind.Null
                ? PortTerm.Null
                : PortTerm.Parse(vector.GetProperty("from").GetString(), PortTermGrammar.Document);
            var to = PortTerm.Parse(vector.GetProperty("to").GetString(), PortTermGrammar.Document);
            var expected = vector.GetProperty("assignable").GetBoolean();
            if (PortTermAssignability.IsAssignable(from, to, vocabulary) != expected)
                wrong.Add($"{from} -> {to}: expected {expected}");
        }

        wrong.Should().BeEmpty();
    }

    [Fact]
    public void Every_vector_that_answers_yes_names_its_conversion_and_none_that_answers_no_does()
    {
        Terms.GetProperty("vectors").EnumerateArray()
            .Where(v => v.GetProperty("assignable").GetBoolean() != v.TryGetProperty("conversion", out _))
            .Select(v => $"{v.GetProperty("from")} -> {v.GetProperty("to")}")
            .Should().BeEmpty();
    }

    [Fact]
    public void The_conversion_rule_names_the_conversion_every_accepted_vector_names()
    {
        var vocabulary = new VectorVocabulary(Terms.GetProperty("vocabulary"));
        var wrong = new List<string>();
        foreach (var vector in Terms.GetProperty("vectors").EnumerateArray().Where(v => v.GetProperty("assignable").GetBoolean()))
        {
            var from = vector.GetProperty("from").ValueKind == JsonValueKind.Null
                ? PortTerm.Null
                : PortTerm.Parse(vector.GetProperty("from").GetString(), PortTermGrammar.Document);
            var to = PortTerm.Parse(vector.GetProperty("to").GetString(), PortTermGrammar.Document);
            var expected = vector.GetProperty("conversion").GetString();
            var actual = PortConversion.Between(from, to, vocabulary.IsValueType).ToString();
            if (actual != expected)
                wrong.Add($"{from} -> {to}: expected {expected}, recorded {actual}");
        }

        wrong.Should().BeEmpty();
    }

    [Fact]
    public void The_value_type_column_of_each_library_atom_is_its_CLR_type()
    {
        foreach (var atom in Terms.GetProperty("vocabulary").GetProperty("atoms").EnumerateObject())
        {
            if (WorkflowTypeVocabulary.Builtin.ClrTypeOf(PortTerm.Atom(atom.Name)) is { } clr)
                clr.IsValueType.Should().Be(atom.Value.GetProperty("valueType").GetBoolean(), atom.Name);
        }
    }

    [Fact]
    public void A_100000_deep_collection_is_answered_on_a_256KB_stack_with_no_recursion()
    {
        var vocabulary = new VectorVocabulary(Terms.GetProperty("vocabulary"));
        static PortTerm Deep(string element) => PortTerm.Parse(
            string.Concat(Enumerable.Repeat("Collection<", 100_000)) + element + string.Concat(Enumerable.Repeat(">", 100_000)),
            PortTermGrammar.Document);
        bool[]? answers = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                answers =
                [
                    PortTermAssignability.IsAssignable(Deep("Int"), Deep("Number"), vocabulary),
                    PortTermAssignability.IsAssignable(Deep("Number"), Deep("Int"), vocabulary),
                    PortTermAssignability.IsAssignable(Deep("Int?"), Deep("Int"), vocabulary),
                ];
            }
            catch (Exception e) { failure = e; }
        }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();

        failure.Should().BeNull();
        answers.Should().Equal(true, false, false);
    }

    [Fact]
    public void A_term_holding_a_type_parameter_or_a_null_target_is_refused()
    {
        var vocabulary = new VectorVocabulary(Terms.GetProperty("vocabulary"));
        var parameter = PortTerm.Parse("$T", PortTermGrammar.Declaration);
        var intTerm = PortTerm.Parse("Int", PortTermGrammar.Document);

        ((Action)(() => PortTermAssignability.IsAssignable(parameter, intTerm, vocabulary)))
            .Should().Throw<ArgumentException>().WithMessage("*closed terms*");
        ((Action)(() => PortTermAssignability.IsAssignable(intTerm, PortTerm.Null, vocabulary)))
            .Should().Throw<ArgumentException>().WithMessage("*never a target*");
    }

    private sealed class VectorVocabulary : IPortTermVocabulary
    {
        private readonly IReadOnlyList<(PortTerm From, PortTerm To)> rows;
        private readonly Dictionary<string, bool> atoms;
        private readonly Dictionary<string, bool> constructors;
        private readonly Dictionary<string, bool> valueTypes;

        public VectorVocabulary(JsonElement vocabulary)
        {
            rows = [.. vocabulary.GetProperty("rows").EnumerateArray().Select(r => (
                PortTerm.Parse(r[0].GetString(), PortTermGrammar.Document), PortTerm.Parse(r[1].GetString(), PortTermGrammar.Document)))];
            atoms = Claims(vocabulary.GetProperty("atoms"));
            constructors = Claims(vocabulary.GetProperty("constructors"));
            valueTypes = ValueTypes(vocabulary.GetProperty("atoms"))
                .Concat(ValueTypes(vocabulary.GetProperty("constructors")))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        }

        public bool HasRow(PortTerm from, PortTerm to) => rows.Any(r => r.From.Equals(from) && r.To.Equals(to));

        public bool IsJsonSerializable(PortTerm head) => head.Kind switch
        {
            PortTermKind.Atom => atoms.GetValueOrDefault(head.Name),
            PortTermKind.Constructor => constructors.GetValueOrDefault(head.Name),
            _ => false,
        };

        public bool IsValueType(PortTerm head) => valueTypes.GetValueOrDefault(head.Name);

        private static Dictionary<string, bool> Claims(JsonElement table) => table.EnumerateObject().ToDictionary(
            p => p.Name, p => p.Value.GetProperty("json").GetBoolean(), StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, bool> ValueTypes(JsonElement table) => table.EnumerateObject().ToDictionary(
            p => p.Name, p => p.Value.GetProperty("valueType").GetBoolean(), StringComparer.OrdinalIgnoreCase);
    }

    private static JsonElement LoadFile()
    {
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(RepositoryFiles.PortTypeAssignabilityPath()));
        return document.RootElement.Clone();
    }
}
