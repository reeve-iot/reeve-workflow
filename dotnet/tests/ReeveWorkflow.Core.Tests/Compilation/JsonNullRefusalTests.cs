using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class JsonNullRefusalTests
{
    private sealed class Dud;

    private sealed class WritesNull : JsonConverter<Dud>
    {
        public override Dud Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();

        public override void Write(Utf8JsonWriter writer, Dud value, JsonSerializerOptions options) => writer.WriteNullValue();
    }

    private static PortConversion ToJson(string target)
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver(), Converters = { new WritesNull() } };
        var dud = new ClosedPortType(PortTerm.Atom("Dud"), typeof(Dud), null, options.GetTypeInfo(typeof(Dud)));
        return PortConversion.Between(
            PortTerm.Atom("Dud"), PortTerm.Parse(target, PortTermGrammar.Document), _ => false, term => term.Name == "Dud" ? dud : null);
    }

    [Fact]
    public void A_value_that_serialises_to_JSON_null_is_refused_on_a_Json_port_that_is_not_nullable()
    {
        var refused = FluentActions.Invoking(() => ToJson("Json").Apply(new Dud())).Should().Throw<PortConversionException>().Which;

        refused.Message.Should().Contain("serialised to JSON null").And.Contain("'Dud'");
        ((IWorkflowCodedException)refused).Code.Should().Be(WorkflowErrorCodes.BlockOutputTypeMismatch);
    }

    [Fact]
    public void The_same_value_is_a_null_on_a_nullable_Json_port()
    {
        ToJson("Json?").Apply(new Dud()).Should().BeNull();
    }

    [Fact]
    public void A_null_passes_the_conversion_unchanged_on_either_port()
    {
        ToJson("Json").Apply(null).Should().BeNull();
        ToJson("Json?").Apply(null).Should().BeNull();
    }

    [Fact]
    public void A_conversion_off_its_term_still_fails_as_any_block_failure_does()
    {
        var refused = FluentActions.Invoking(() => ToJson("Json").Apply("not a Dud")).Should().Throw<PortConversionException>().Which;

        ((IWorkflowCodedException)refused).Code.Should().Be(WorkflowErrorCodes.BlockFailed);
    }

    [Fact]
    public void The_refusal_on_a_wire_names_the_receiver_input_the_producer_and_its_output()
    {
        var edge = new CompiledEdge("bad", [new WorkflowMappingDefinition("D", "In")])
        {
            Conversions = new Dictionary<string, PortConversion> { ["In"] = ToJson("Json") },
        };
        var receiver = new CompiledNode("sink", null!, new Dictionary<string, object?>(), new Dictionary<string, CompiledExpression>(),
            [edge], new Dictionary<string, CanvasPath>(), null!, null!, [], []);

        var refused = FluentActions.Invoking(() => receiver.BindWire("bad", "In", new Dud())).Should().Throw<PortConversionException>().Which;

        refused.Message.Should().Contain("Input 'In' of 'sink' from 'bad' (output 'D')").And.Contain("serialised to JSON null");
        ((IWorkflowCodedException)refused).Code.Should().Be(WorkflowErrorCodes.BlockOutputTypeMismatch);
    }
}
