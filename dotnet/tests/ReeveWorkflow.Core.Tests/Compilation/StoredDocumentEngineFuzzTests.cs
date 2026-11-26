using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using Xunit.Abstractions;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class StoredDocumentEngineFuzzTests(ITestOutputHelper output)
{
    private static readonly IBlockShapeSource Catalogue = CompilerTestGraphs.Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
        Shape("dyn", hasDynamicPorts: true, source: "plugin:dyn"),
        Shape("container", supportsDeclaredOutputs: true,
            subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
            subCanvasNames: ["Then"]));

    private sealed class Gen(Random r)
    {
        public int Depth;
        string Key(string k)
        {
            var roll = r.Next(40);
            if (roll == 0) return char.ToUpperInvariant(k[0]) + k[1..];
            if (roll == 1) return k.ToUpperInvariant();
            if (roll == 2) return EscapeFirst(k);
            if (roll == 3) return "\\u" + ((int)k[0]).ToString("X4") + k[1..];
            return k;
        }
        static string EscapeFirst(string k) => "\\u" + ((int)k[0]).ToString("x4") + k[1..];
        string Str(string s) => JsonSerializer.Serialize(s);
        string Num()
        {
            string[] n = ["0", "1", "-0", "1.5", "1e400", "-1e400", "1E+2", "123456789012345678901234567890", "1e-400", "0.0000001", "3"];
            return n[r.Next(n.Length)];
        }
        string Type()
        {
            string[] t = ["Int", "String", "CollectionInt", "Collection", "Collection<Int>", "Collection<Collection<Any>>", "", " Int", "Pt", "Collection<Int", "any", "Device"];
            return r.Next(12) == 0 ? "null" : Str(t[r.Next(t.Length)]);
        }
        public string PortList()
        {
            var sb = new StringBuilder("[");
            var n = r.Next(3);
            for (var i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(',');
                var parts = new List<string> { $"\"{Key("name")}\":{Str("p" + i)}" };
                if (r.Next(4) != 0) parts.Add($"\"{Key("type")}\":{Type()}");
                if (r.Next(3) == 0) parts.Add($"\"{Key("isRequired")}\":{(r.Next(2) == 0 ? "true" : "false")}");
                Shuffle(parts);
                sb.Append('{').Append(string.Join(',', parts)).Append('}');
            }
            return sb.Append(']').ToString();
        }
        void Shuffle(List<string> l) { for (var i = l.Count - 1; i > 0; i--) { var j = r.Next(i + 1); (l[i], l[j]) = (l[j], l[i]); } }
        string Deep(int levels) => new string('[', levels) + new string(']', levels);
        string Config()
        {
            var parts = new List<string>();
            if (r.Next(2) == 0) parts.Add($"\"InputPorts\":{Str(PortList())}");
            if (r.Next(2) == 0) parts.Add($"\"OutputPorts\":{Str(PortList())}");
            if (r.Next(3) == 0) parts.Add($"\"DeviceId\":{Str("d")}");
            if (r.Next(6) == 0) parts.Add($"\"Kind\":{{\"Nodes\":1,\"nodes\":2}}");
            if (r.Next(8) == 0) parts.Add($"\"inputPorts\":{Str(PortList())}");
            if (r.Next(10) == 0) parts.Add($"\"deep\":{Deep(r.Next(55, 66) - Depth)}");
            if (r.Next(4) == 0)
            {
                parts.Add($"\"{(r.Next(6) == 0 ? "ErrorPolicy" : "errorPolicy")}\":" + new[]
                {
                    "null", "{}", "{\"policy\":\"skip\"}", "{\"Policy\":\"EmitError\",\"routeToErrorPort\":true}", "\"Skip\"",
                    "{\"policy\":\"Bogus\"}", "{\"policy\":\"Skip\",\"routeToErrorPort\":true}", "{\"policy\":\"ContinueWithDefault\",\"defaultOutputJson\":\"{}\"}",
                    "[]", "{\"policy\":1,\"POLICY\":2}",
                }[r.Next(10)]);
            }

            Shuffle(parts);
            return "{" + string.Join(',', parts) + "}";
        }
        string Node(int i, int level)
        {
            var parts = new List<string>
            {
                $"\"{Key("id")}\":{Str("n" + level + "_" + i)}",
                $"\"{Key("blockTypeId")}\":{Str(new[] { "dyn", "container", "WorkflowInput", "RunScript", "fixed" }[r.Next(5)])}",
            };
            parts.Add($"\"{Key("position")}\":{{\"{Key("x")}\":{Num()},\"{Key("y")}\":{Num()}}}");
            var configRoll = r.Next(20);
            if (configRoll == 0) parts.Add($"\"{Key("config")}\":{Config()}");
            else if (configRoll == 1) parts.Add($"\"{Key("configuration")}\":null");
            else parts.Add($"\"{Key("configuration")}\":{Config()}");
            if (level < 3 && r.Next(4) == 0) parts.Add($"\"{Key("subCanvases")}\":{{\"Then\":{Canvas(level + 1)}}}");
            if (r.Next(4) == 0) parts.Add($"\"{Key("outputPorts")}\":[{{\"{Key("name")}\":\"o\",\"{Key("displayName")}\":\"O\",\"{Key("type")}\":{Type()},\"{Key("sources")}\":{{\"Then\":\"raw\"}}}}]");
            if (r.Next(6) == 0) parts.Add($"\"{Key("errorPolicy")}\":{{\"{Key("policy")}\":\"{new[] { "Skip", "skip", "EmitError", "FailWorkflow" }[r.Next(4)]}\",\"{Key("routeToErrorPort")}\":true}}");
            if (r.Next(6) == 0) parts.Add($"\"{Key("passthrough")}\":[\"a\"]");
            Shuffle(parts);
            return "{" + string.Join(',', parts) + "}";
        }
        string Edge(int i, int n, int level)
        {
            var parts = new List<string>
            {
                $"\"{Key("id")}\":\"e{i}\"",
                $"\"{Key("sourceNodeId")}\":\"n{level}_{r.Next(n)}\"",
                $"\"{Key("targetNodeId")}\":\"n{level}_{r.Next(n)}\"",
            };
            var m = r.Next(10);
            if (m == 0) parts.Add($"\"{Key("mappings")}\":[{{\"{Key("sourceOutput")}\":\"o\",\"{Key("targetInput")}\":\"i\"}}]");
            else if (m == 1) parts.Add($"\"{Key("mappings")}\":null");
            else if (m == 2) parts.Add($"\"{Key("mappings")}\":{{\"i2\":{{\"{Key("sourceOutputName")}\":\"o\"}}}}");
            else if (m <= 5) parts.Add($"\"{Key("mappings")}\":{{\"In\":{{\"{Key("type")}\":\"literal\",\"{Key("literalValue")}\":{{\"Nodes\":[1]}}}},\"i2\":{{\"{Key("type")}\":\"direct\",\"{Key("sourceOutputName")}\":\"o\"}}}}");
            else parts.Add($"\"{Key("mappings")}\":{{}}");
            Shuffle(parts);
            return "{" + string.Join(',', parts) + "}";
        }
        public string Canvas(int level)
        {
            Depth = level * 4;
            var n = r.Next(0, 4);
            var parts = new List<string>();
            if (r.Next(30) != 0) parts.Add($"\"{Key("nodes")}\":[" + string.Join(',', Enumerable.Range(0, n).Select(i => Node(i, level))) + "]");
            if (r.Next(12) != 0) parts.Add($"\"{Key(r.Next(10) == 0 ? "connections" : "edges")}\":[" + string.Join(',', Enumerable.Range(0, n == 0 ? 0 : r.Next(3)).Select(i => Edge(i, n, level))) + "]");
            if (r.Next(40) == 0) parts.Add($"\"{Key("outputMappings")}\":[{{\"sourceNodeId\":\"n\",\"sourceOutput\":\"o\",\"workflowOutput\":\"w\"}}]");
            Shuffle(parts);
            return "{" + string.Join(',', parts) + "}";
        }
        public string Params()
        {
            if (r.Next(6) == 0) return "null";
            var n = r.Next(3);
            return "[" + string.Join(',', Enumerable.Range(0, n).Select(i => $"{{\"{Key("name")}\":\"a{i}\",\"{Key("type")}\":{Type()},\"{Key("isRequired")}\":true}}")) + "]";
        }
    }

    private static readonly string[] Frags = ["{", "}", "[", "]", "\"", ",", ":", "null", "\\ud800", "\ud800", "\\", "\uFEFF", "/*c*/", ",]", "NaN", "Infinity", "\"Nodes\":[{\"blockTypeId\":\"X\"}],"];

    private static string Mutate(Random r, string t)
    {
        var k = r.Next(1, 3);
        for (var i = 0; i < k; i++)
        {
            var at = r.Next(t.Length + 1);
            t = r.Next(3) switch
            {
                0 when t.Length > 0 => t.Remove(Math.Min(at, t.Length - 1), 1),
                _ => t.Insert(at, Frags[r.Next(Frags.Length)]),
            };
        }
        return t;
    }

    public static IEnumerable<(string Def, string Params)> Corpus(int n, int seed)
    {
        var r = new Random(seed);
        for (var i = 0; i < n; i++)
        {
            var g = new Gen(r);
            var d = g.Canvas(0);
            var p = g.Params();
            if (r.Next(4) == 0) d = Mutate(r, d);
            if (r.Next(200) == 0) d = r.Next(2) == 0 ? "null" : " null\n";
            if (r.Next(6) == 0) p = Mutate(r, p);
            yield return (d, p);
        }
    }

    private static string AuthoredPolicy(WorkflowNodeDefinition node)
    {
        if (BoundaryBlockShapes.IsBoundary(node.BlockTypeId))
            return "none";

        var policy = node.ErrorPolicy;
        if (policy is null && node.Config?.TryGetValue("errorPolicy", out var configured) == true)
        {
            try
            {
                policy = configured.Deserialize<NodeErrorConfig>(WorkflowGraphHelpers.JsonOptions);
            }
            catch (JsonException)
            {
                return "unread " + configured.GetRawText();
            }
        }

        return policy is null ? "none" : $"{policy.Policy}/{policy.DefaultOutputJson ?? "null"}";
    }

    private static string Canon(WorkflowDefinition? d, bool maskDeclaredTypes = false)
    {
        if (d is null) return "<null>";
        var sb = new StringBuilder();
        void C(WorkflowDefinition c)
        {
            sb.Append("{N:");
            foreach (var n in c.Nodes)
            {
                sb.Append('(').Append(n.Id).Append('|').Append(n.BlockTypeId).Append('|');
                foreach (var (k, v) in (n.Config ?? []).Where(c => c.Key != "errorPolicy" && c.Value.ValueKind != JsonValueKind.Null)) sb.Append(k).Append('=').Append(k is "InputPorts" or "OutputPorts" ? "<list>" : v.GetRawText()).Append(';');
                sb.Append("|op:").Append(maskDeclaredTypes
                    ? System.Text.RegularExpressions.Regex.Replace(JsonSerializer.Serialize(n.OutputPorts), "\"(?i:type)\":(\"[^\"]*\"|null)", "\"type\":<t>")
                    : JsonSerializer.Serialize(n.OutputPorts)).Append("|ep:").Append(AuthoredPolicy(n));
                sb.Append("|pt:").Append(JsonSerializer.Serialize(n.Passthrough));
                foreach (var (name, sub) in n.SubCanvases ?? []) { sb.Append('[').Append(name).Append(':'); C(sub); sb.Append(']'); }
                sb.Append(')');
            }
            sb.Append(" E:");
            foreach (var e in c.GetConnections() ?? []) sb.Append($"({e.Id}|{e.SourceNodeId}|{e.TargetNodeId}|{(e.Mappings is { } m ? string.Join(",", WorkflowGraphHelpers.ReadMappings(m).Select(x => x.ToString())) : "")})");
            sb.Append(" O:").Append(JsonSerializer.Serialize(c.OutputMappings)).Append('}');
        }
        C(d);
        return sb.ToString();
    }

    private static bool Binds(string text, out WorkflowDefinition? model)
    {
        try
        {
            model = JsonSerializer.Deserialize<WorkflowDefinition>(text, WorkflowGraphHelpers.JsonOptions);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            model = null;
            return false;
        }
    }

    private static IEnumerable<string> Blocks(WorkflowDefinition? c) =>
        (c?.Nodes ?? []).Where(n => n is not null).SelectMany(n => (n.SubCanvases?.Values.SelectMany(Blocks) ?? []).Append(n.BlockTypeId));

    [Fact]
    public void On_twenty_thousand_documents_nothing_throws_and_the_reader_the_engine_and_the_migration_agree()
    {
        int accepted = 0, refused = 0, total = 0, bindsNull = 0;
        var faults = new Dictionary<string, int>();
        var problems = new List<string>();
        foreach (var (def, par) in Corpus(20000, 424242))
        {
            total++;
            void Guard(string what, Action a)
            {
                try { a(); }
                catch (Exception ex) { problems.Add($"{what} THREW {ex.GetType().Name}: {ex.Message.Split('\n')[0]} :: {def} :: {par}"); }
            }

            StoredWorkflowDocument? doc = null;
            StoredDocumentFault? fault = null;
            Guard("Read", () => doc = StoredWorkflowDocument.Read(def, out fault));
            Guard("ReadParameters", () => StoredWorkflowDocument.ReadParameters(par, PortTypeNameSite.InputParameter, out _));
            Guard("Check", () => PortTypeNameCheck.Check(def, par, par));
            Guard("Check-nulls", () => PortTypeNameCheck.Check(def, null, null));

            if (Binds(def, out var bound) && bound is null)
            {
                bindsNull++;
                if (doc is not null)
                    problems.Add($"NULL the engine binds no model, and the reader misses it :: {def}");
            }

            if (doc is null)
            {
                refused++;
                if (fault is not null) faults[fault.Rule] = faults.GetValueOrDefault(fault.Rule) + 1;
            }
            else
            {
                accepted++;
                CompilationResult? a = null, b = null;
                Guard("CompileDoc", () => a = WorkflowCompiler.Compile(doc, Catalogue, CanvasSignature.Empty));
                Guard("CompileModel", () => b = WorkflowCompiler.Compile(doc.Definition, Catalogue, CanvasSignature.Empty));
                if (a is not null && b is not null)
                {
                    var ka = string.Join(";", a.Diagnostics.Select(d => d.RuleId + "@" + d.NodeId).Order());
                    var kb = string.Join(";", b.Diagnostics.Select(d => d.RuleId + "@" + d.NodeId).Order());
                    if (ka != kb) problems.Add((def.Contains("e400") ? "E400 " : "") + $"COMPILE doc vs model differ: [{ka}] vs [{kb}] :: {def}");
                }

                WorkflowDefinition? engine = null;
                try { engine = JsonSerializer.Deserialize<WorkflowDefinition>(def, WorkflowGraphHelpers.JsonOptions); }
                catch (Exception ex) { problems.Add($"ENGINE refuses accepted doc: {ex.GetType().Name} {ex.Message.Split('\n')[0]} :: {def}"); }
                if (engine is not null && Canon(engine) != Canon(doc.Definition))
                    problems.Add($"ENGINE model differs :: {def}");
                try
                {
                    var col = WorkflowDefinitionBlockTypes.Collect(def);
                    var missing = Blocks(doc.Definition).Where(x => x.Length > 0).Except(col, StringComparer.OrdinalIgnoreCase).ToList();
                    if (missing.Count > 0) problems.Add($"COLLECT misses {string.Join(",", missing)} :: {def}");
                }
                catch (Exception ex) { problems.Add($"COLLECT threw on accepted {ex.GetType().Name} :: {def}"); }
            }
        }

        output.WriteLine($"total={total} accepted={accepted} refused={refused} bindsNull={bindsNull}");
        bindsNull.Should().BePositive("the corpus must reach the literal null");
        accepted.Should().BeGreaterThan(total / 10, "the corpus must reach the compiler, not only the lexer");
        refused.Should().BeGreaterThan(total / 10);
        output.WriteLine(string.Join(" ", faults.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        foreach (var g in problems.GroupBy(p => p.Split("::")[0]).OrderByDescending(g => g.Count()))
            output.WriteLine($"{g.Count()} x {g.Key}\n    e.g. {g.First()}");
        problems.Should().BeEmpty();
    }
}
