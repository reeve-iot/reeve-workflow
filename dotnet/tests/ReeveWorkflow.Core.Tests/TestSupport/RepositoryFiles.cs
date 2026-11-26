using System.Globalization;
using System.Text;

namespace ReeveWorkflow.Core.Tests.TestSupport;

internal static class RepositoryFiles
{
    public static string LiteralCoercionPath() => Locate("literal-coercion.json");
    public static string ValidationCorpusPath() => Locate("workflow-validation-corpus.json");

    public static string DanglingEdgeAgreementPath() => Locate("dangling-edge-agreement.json");

    public static string RuleAgreementPath() => Locate("rule-agreement.json");

    public static string RuleParityPath() => Locate("rule-parity.json");

    public static string ExpressionTypingPath() => Locate("expression-typing.json");

    public static string RefusalCodesPath() => Locate("refusal-codes.json");

    public static string ExpressionFunctionsPath() => Locate("expression-functions.json");

    public static string StoredDocumentGrammarPath() => Locate("stored-document-grammar.json");

    public static string ReaderSourceDirectory() =>
        LocateDirectory("dotnet", "src", "ReeveWorkflow.Core", "Compilation", "StoredDocument");

    public static string WorkflowDocumentCorpusPath() => Locate("workflow-documents.json");

    public static string PortTypeRegistrationCorpusPath() => Locate("port-type-registration.json");

    public static string TypeParameterBindingPath() => Locate("type-parameter-binding.json");

    public static string RegistrationAgreementPath() => Locate("registration-agreement.json");

    public static string PortTypeAssignabilityPath() => Locate("port-type-assignability.json");

    public static string JsonProjectionPath() => Locate("json-projection.json");

    public static string RepeatedMemberPath() => Locate("repeated-member.json");

    public static string PortTypeNameCorpusPath() => Locate("port-type-names.tsv");

    public static IReadOnlyList<string> PortTypeNameCorpusNames() =>
        [.. File.ReadLines(PortTypeNameCorpusPath())
            .Where(line => line.Length > 0 && line[0] != '#')
            .Select(line => UnescapeCorpusField(line.Split('\t')[0]))];

    public static string UnescapeCorpusField(string field)
    {
        if (!field.Contains("\\u", StringComparison.Ordinal)) return field;
        var builder = new StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 5 < field.Length + 1 && field[i + 1] == 'u')
            {
                var code = ushort.Parse(field.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                builder.Append((char)code);
                i += 5;
            }
            else
            {
                builder.Append(field[i]);
            }
        }
        return builder.ToString();
    }

    public static bool ContractExists(string fileName) => TryLocate(fileName) is not null;

    private static string Locate(string fileName) =>
        TryLocate(fileName) ?? throw new FileNotFoundException($"contracts/{fileName} was not found above " + AppContext.BaseDirectory);

    private static string LocateDirectory(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"'{Path.Combine(parts)}' was not found above " + AppContext.BaseDirectory);
    }

    private static string? TryLocate(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "contracts", fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
