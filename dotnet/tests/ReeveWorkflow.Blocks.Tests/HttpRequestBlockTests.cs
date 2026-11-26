using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class HttpRequestBlockTests
{
    [Fact]
    public async Task Returns_the_status_body_and_success_of_a_200()
    {
        var block = Sending(_ => Respond(HttpStatusCode.OK, "{\"ok\":true}"));

        var result = await block.ExecuteAsync(Inputs("https://example.com/api"), Context(), CancellationToken.None);

        result["Success"].Should().Be(true);
        result["StatusCode"].Should().Be(200L);
        result["Body"].Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task Reports_a_404_as_a_completed_request_that_did_not_succeed()
    {
        var block = Sending(_ => Respond(HttpStatusCode.NotFound, "Not Found"));

        var result = await block.ExecuteAsync(Inputs("https://example.com/missing"), Context(), CancellationToken.None);

        result["Success"].Should().Be(false);
        result["StatusCode"].Should().Be(404L);
    }

    [Fact]
    public async Task Reports_a_connection_failure_without_a_status()
    {
        var block = Sending(_ => throw new HttpRequestException("Connection refused"));

        var result = await block.ExecuteAsync(Inputs("https://unreachable.local"), Context(), CancellationToken.None);

        result["Success"].Should().Be(false);
        result["StatusCode"].Should().Be(0L);
        ((string?)result["Body"]).Should().Contain("Connection refused");
    }

    [Fact]
    public async Task Sends_the_method_and_body_it_was_given()
    {
        HttpMethod? method = null;
        string? body = null;
        var block = Sending(async req =>
        {
            method = req.Method;
            body = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return await Respond(HttpStatusCode.Created, "");
        });

        await block.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Url"] = "https://example.com/api", ["Method"] = "POST", ["Body"] = "{\"name\":\"test\"}"
            },
            Context(), CancellationToken.None);

        method.Should().Be(HttpMethod.Post);
        body.Should().Be("{\"name\":\"test\"}");
    }
    [Fact]
    public async Task Sends_one_field_per_entry_with_its_value_trimmed_of_spaces_and_tabs_only()
    {
        var seen = new List<HttpRequestMessage>();
        var block = Sending(req => { seen.Add(req); return Respond(HttpStatusCode.OK, ""); });

        await block.ExecuteAsync(
            WithHeaders(("X-Api-Key", "secret123"), ("X-Padded", "\t a b \t"), ("X-Tight", "v"), ("X-Colon", "a:b")),
            Context(), CancellationToken.None);

        var sent = seen.Should().ContainSingle().Which.Headers;
        sent.GetValues("X-Api-Key").Should().Equal("secret123");
        sent.GetValues("X-Padded").Should().Equal("a b");
        sent.GetValues("X-Tight").Should().Equal("v");
        sent.GetValues("X-Colon").Should().Equal("a:b");
        sent.Select(h => h.Key).Should().Equal("X-Api-Key", "X-Padded", "X-Tight", "X-Colon");
    }

    [Fact]
    public async Task An_empty_value_is_sent_as_an_empty_field()
    {
        var seen = new List<HttpRequestMessage>();
        var block = Sending(req => { seen.Add(req); return Respond(HttpStatusCode.OK, ""); });

        await block.ExecuteAsync(WithHeaders(("X-Empty", ""), ("X-Blank", " \t ")), Context(), CancellationToken.None);

        var sent = seen.Should().ContainSingle().Which.Headers;
        sent.GetValues("X-Empty").Should().Equal("");
        sent.GetValues("X-Blank").Should().Equal("");
    }

    [Theory]
    [InlineData("Accept", "accept")]
    [InlineData("X-Tag", "X-TAG")]
    [InlineData("content-type", "Content-Type")]
    public async Task Two_keys_naming_one_field_fail_the_node_naming_both_and_send_nothing(string first, string second)
    {
        var sent = 0;
        var block = Sending(_ => { sent++; return Respond(HttpStatusCode.OK, ""); });
        var inputs = WithHeaders(("X-Ok", "1"), (first, "v-first"), (second, "v-second"));
        inputs["Method"] = "POST";
        inputs["Body"] = "{}";

        var act = async () => await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Be($"Input 'Headers' keys '{first}' and '{second}' name one field; a field is sent once or refused.");
        message.Should().NotContain("v-first").And.NotContain("v-second");
        sent.Should().Be(0);
    }

    [Fact]
    public async Task No_entry_sends_no_header_of_its_own()
    {
        var seen = new List<HttpRequestMessage>();
        var block = Sending(req => { seen.Add(req); return Respond(HttpStatusCode.OK, ""); });

        await block.ExecuteAsync(WithHeaders(), Context(), CancellationToken.None);
        await block.ExecuteAsync(Inputs("https://example.com/api"), Context(), CancellationToken.None);

        seen.Should().HaveCount(2).And.OnlyContain(r => !r.Headers.Any());
    }

    public static TheoryData<object> NotMaps => new()
    {
        "X-Key: k",
        "{\"X-Key\":\"k\"}",
        new List<string> { "X-Key: k" },
        new Dictionary<string, object?> { ["X-Key"] = "k" },
        System.Text.Json.JsonDocument.Parse("{\"X-Key\":\"k\"}").RootElement.Clone(),
    };

    [Theory]
    [MemberData(nameof(NotMaps))]
    public async Task Headers_that_are_not_a_map_of_text_fail_the_node_naming_the_input(object value)
    {
        var sent = 0;
        var block = Sending(_ => { sent++; return Respond(HttpStatusCode.OK, ""); });
        var inputs = Inputs("https://example.com/api");
        inputs["Headers"] = value;

        var act = async () => await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be("Input 'Headers' must be a map of field names to values.");
        sent.Should().Be(0);
    }

    [Fact]
    public async Task Content_Type_whatever_its_case_sets_the_body_media_type_and_is_no_request_header()
    {
        var seen = new List<HttpRequestMessage>();
        var block = Sending(req => { seen.Add(req); return Respond(HttpStatusCode.OK, ""); });
        var inputs = WithHeaders(("CONTENT-TYPE", "text/csv"));
        inputs["Method"] = "POST";
        inputs["Body"] = "a,b";

        await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        var request = seen.Should().ContainSingle().Subject;
        request.Content!.Headers.ContentType!.MediaType.Should().Be("text/csv");
        request.Headers.Should().BeEmpty();
    }

    [Fact]
    public async Task A_body_without_a_Content_Type_entry_is_sent_as_application_json()
    {
        var seen = new List<HttpRequestMessage>();
        var block = Sending(req => { seen.Add(req); return Respond(HttpStatusCode.OK, ""); });
        var inputs = WithHeaders(("X-Key", "k"));
        inputs["Method"] = "POST";
        inputs["Body"] = "{}";

        await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        seen.Should().ContainSingle().Which.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
    }


    [Fact]
    public async Task A_request_that_outlives_its_timeout_is_reported_as_timed_out_not_cancelled()
    {
        var block = Sending(async req =>
        {
            await Task.Delay(Timeout.Infinite, req.Options.TryGetValue(SendToken, out var t) ? t : default);
            return await Respond(HttpStatusCode.OK, "");
        });

        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://example.com/slow", ["TimeoutSeconds"] = 1L },
            Context(), CancellationToken.None);

        result["Success"].Should().Be(false);
        result["Body"].Should().Be("Request timed out.");
    }

    [Fact]
    public async Task A_missing_url_refuses_rather_than_sending_nowhere()
    {
        var block = Sending(_ => Respond(HttpStatusCode.OK, ""));

        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?>(), Context(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Url*");
    }

    [Fact]
    public async Task An_unparseable_url_is_a_failed_result_rather_than_a_crash()
    {
        var block = Sending(_ => Respond(HttpStatusCode.OK, ""));

        var result = await block.ExecuteAsync(Inputs("not a valid url!@#$"), Context(), CancellationToken.None);

        result["Success"].Should().Be(false);
        result["StatusCode"].Should().Be(0L);
        ((string?)result["Body"]).Should().Contain("Invalid URL");
    }

    [Fact]
    public async Task A_parameterized_content_type_survives()
    {
        var seen = new List<string?>();
        var block = Sending(req =>
        {
            seen.Add(req.Content?.Headers.ContentType?.ToString());
            return Respond(HttpStatusCode.OK, "");
        });
        var inputs = WithHeaders(("Content-Type", "application/json; charset=utf-8"));
        inputs["Method"] = "POST";
        inputs["Body"] = "{}";

        await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        seen.Should().Equal("application/json; charset=utf-8");
    }

    [Theory]
    [InlineData("totally not a media type ;;;")]
    [InlineData("")]
    public async Task A_Content_Type_that_is_not_a_media_type_fails_the_node_and_sends_nothing(string value)
    {
        var sent = 0;
        var block = Sending(_ => { sent++; return Respond(HttpStatusCode.OK, ""); });
        var inputs = WithHeaders(("Content-Type", value));
        inputs["Method"] = "POST";
        inputs["Body"] = "{}";

        var act = async () => await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should()
            .Be("Input 'Headers' key 'Content-Type' has a Content-Type that is not a media type.");
        sent.Should().Be(0);
    }

    [Theory]
    [InlineData("a\r\nX-Injected: b")]
    [InlineData("a\nX-Injected: b")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    [InlineData("\r\nX-Injected: b")]
    [InlineData("a\u000bb")]
    [InlineData("a\u007fb")]
    [InlineData("a\r\n")]
    [InlineData("a\u00a0b")]
    [InlineData("\u00a0v")]
    [InlineData("v\u00a0")]
    [InlineData("a\u0085b")]
    [InlineData("\u0080")]
    [InlineData("ł")]
    [InlineData("v\u200b")]
    [InlineData("a\U0001F600b")]
    public async Task A_value_holding_a_character_other_than_visible_ASCII_fails_the_node_naming_the_key_before_anything_is_sent(string value)
    {
        var sent = 0;
        var block = Sending(_ => { sent++; return Respond(HttpStatusCode.OK, ""); });

        var act = async () => await block.ExecuteAsync(WithHeaders(("X-Ok", "1"), ("X-A", value)), Context(), CancellationToken.None);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Be("Input 'Headers' key 'X-A' has a value with a character other than visible ASCII, a space or a tab, which a field value cannot hold.");
        sent.Should().Be(0);
    }

    [Fact]
    public async Task A_refused_value_fails_the_node_whatever_ThrowOnFailure_says()
    {
        var block = Sending(_ => Respond(HttpStatusCode.OK, ""));
        var inputs = WithHeaders(("X-A", "a\r\nX-Injected: b"));
        inputs["ThrowOnFailure"] = false;

        var act = async () => await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_tab_inside_a_value_is_kept()
    {
        var seen = new List<HttpRequestMessage>();
        var block = Sending(req => { seen.Add(req); return Respond(HttpStatusCode.OK, ""); });

        await block.ExecuteAsync(WithHeaders(("X-Inner", " a\tb ")), Context(), CancellationToken.None);

        seen.Should().ContainSingle().Which.Headers.GetValues("X-Inner").Should().Equal("a\tb");
    }

    [Fact]
    public async Task A_null_value_fails_the_node_naming_the_key_and_saying_it_has_no_value()
    {
        var block = Sending(_ => Respond(HttpStatusCode.OK, ""));
        var inputs = Inputs("https://example.com/api");
        inputs["Headers"] = new Dictionary<string, string> { ["X-A"] = null! };

        var act = async () => await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be("Input 'Headers' key 'X-A' has no value.");
    }

    [Fact]
    public async Task A_name_may_use_every_token_character()
    {
        const string name = "!#$%&'*+-.^_`|~0aZ";
        var seen = new List<HttpRequestMessage>();
        var block = Sending(req => { seen.Add(req); return Respond(HttpStatusCode.OK, ""); });

        await block.ExecuteAsync(WithHeaders((name, "v")), Context(), CancellationToken.None);

        seen.Should().ContainSingle().Which.Headers.GetValues(name).Should().Equal("v");
    }

    [Theory]
    [InlineData("X(A)", "X", 2)]
    [InlineData("X[A]", "X", 2)]
    [InlineData("X\"A", "X", 2)]
    [InlineData("X/A", "X", 2)]
    [InlineData("X,A", "X", 2)]
    [InlineData("X;A", "X", 2)]
    [InlineData("X<A>", "X", 2)]
    [InlineData("X=A", "X", 2)]
    [InlineData("X?A", "X", 2)]
    [InlineData("X@A", "X", 2)]
    [InlineData("X\\A", "X", 2)]
    [InlineData("X{A}", "X", 2)]
    [InlineData("Xł", "X", 2)]
    [InlineData("X Key", "X", 2)]
    [InlineData("X-Key:", "X-Key", 6)]
    [InlineData("X-A\r\nX-B", "X-A", 4)]
    public async Task A_key_that_is_no_token_fails_the_node_naming_its_token_prefix_and_the_bad_position(string name, string prefix, int position)
    {
        var sent = 0;
        var block = Sending(_ => { sent++; return Respond(HttpStatusCode.OK, ""); });

        var act = async () => await block.ExecuteAsync(WithHeaders(("X-Ok", "1"), (name, "v")), Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be(
            $"Input 'Headers' key '{prefix}...' is not a field name; a name is a token, and its character {position} is not a token character.");
        sent.Should().Be(0);
    }

    [Theory]
    [InlineData(" X")]
    [InlineData(":X")]
    public async Task A_key_that_starts_with_no_token_character_fails_the_node_naming_its_row_and_not_the_key(string name)
    {
        var block = Sending(_ => Respond(HttpStatusCode.OK, ""));

        var act = async () => await block.ExecuteAsync(WithHeaders(("X-Ok", "1"), ("Accept", "*/*"), (name, "v")), Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be(
            "Input 'Headers' row 3 has a key that is not a field name; a name is a token, and its character 1 is not a token character.");
    }

    [Fact]
    public async Task An_empty_key_fails_the_node_naming_its_row()
    {
        var block = Sending(_ => Respond(HttpStatusCode.OK, ""));

        var act = async () => await block.ExecuteAsync(WithHeaders(("X-Ok", "1"), ("", "v")), Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be(
            "Input 'Headers' row 2 has an empty key; a name is a token, which is at least one character.");
    }

    [Fact]
    public void A_pasted_field_line_as_a_stored_key_is_refused_at_compile_without_echoing_the_secret()
    {
        var definition = OneRequest(JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["Authorization: Bearer x"] = "" }));

        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(Standard()), CanvasSignature.Empty);

        var found = result.Diagnostics.Should().ContainSingle().Which;
        found.RuleId.Should().Be("secret-input-key-invalid");
        found.Message.Should().NotContain("Bearer x");
    }

    [Fact]
    public async Task A_content_header_entry_with_a_body_is_sent_on_the_body()
    {
        var seen = new List<HttpRequestMessage>();
        var block = Sending(req => { seen.Add(req); return Respond(HttpStatusCode.OK, ""); });
        var inputs = WithHeaders(("Content-Language", "pl"), ("Content-Disposition", "attachment"),
            ("Expires", "Wed, 21 Oct 2026 07:28:00 GMT"), ("X-K", "v"));
        inputs["Method"] = "POST";
        inputs["Body"] = "{}";

        await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        var request = seen.Should().ContainSingle().Subject;
        request.Content!.Headers.ContentLanguage.Should().Equal("pl");
        request.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        request.Content.Headers.Expires.Should().Be(new DateTimeOffset(2026, 10, 21, 7, 28, 0, TimeSpan.Zero));
        request.Headers.Select(h => h.Key).Should().Equal("X-K");
    }

    [Theory]
    [InlineData("Content-Language", "pl")]
    [InlineData("content-type", "text/plain")]
    [InlineData("Expires", "Wed, 21 Oct 2026 07:28:00 GMT")]
    [InlineData("Last-Modified", "Wed, 21 Oct 2026 07:28:00 GMT")]
    public async Task A_content_header_entry_without_a_body_fails_the_node_naming_the_key_and_sends_nothing(string name, string value)
    {
        var sent = 0;
        var block = Sending(_ => { sent++; return Respond(HttpStatusCode.OK, ""); });

        var act = async () => await block.ExecuteAsync(WithHeaders(("X-Ok", "1"), (name, value)), Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should()
            .Be($"Input 'Headers' key '{name}' is a header of the body, and the request has no Body.");
        sent.Should().Be(0);
    }

    [Theory]
    [InlineData("Content-Length", false)]
    [InlineData("content-length", true)]
    [InlineData("Transfer-Encoding", false)]
    [InlineData("transfer-encoding", true)]
    [InlineData("Host", false)]
    [InlineData("HOST", true)]
    [InlineData("Connection", false)]
    [InlineData("connection", true)]
    [InlineData("Upgrade", false)]
    [InlineData("upgrade", true)]
    [InlineData("Keep-Alive", false)]
    [InlineData("keep-alive", true)]
    [InlineData("Proxy-Connection", false)]
    [InlineData("proxy-connection", true)]
    [InlineData("TE", false)]
    [InlineData("te", true)]
    [InlineData("Content-Encoding", false)]
    [InlineData("content-encoding", true)]
    [InlineData("Accept-Encoding", false)]
    [InlineData("accept-encoding", true)]
    [InlineData("Expect", false)]
    [InlineData("expect", true)]
    [InlineData("Trailer", false)]
    [InlineData("trailer", true)]
    public async Task A_field_the_transport_owns_fails_the_node_naming_the_key_and_sends_nothing(string name, bool withBody)
    {
        var sent = 0;
        var block = Sending(_ => { sent++; return Respond(HttpStatusCode.OK, ""); });
        var inputs = WithHeaders((name, "chunked"));
        if (withBody)
        {
            inputs["Method"] = "POST";
            inputs["Body"] = "{}";
        }

        var act = async () => await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Be($"Input 'Headers' key '{name}' is a field the block takes from the Url, the Body or the connection, so an entry cannot set it.");
        message.Should().NotContain("chunked");
        sent.Should().Be(0);
    }

    [Theory]
    [InlineData("text/csv; charset=windows-1250")]
    [InlineData("text/plain; charset=utf8")]
    [InlineData("text/plain; charset=UTF-16")]
    [InlineData("text/plain; charset=\"iso-8859-1\"")]
    public async Task A_Content_Type_charset_other_than_utf_8_fails_the_node_naming_the_key_and_sends_nothing(string value)
    {
        var sent = 0;
        var block = Sending(_ => { sent++; return Respond(HttpStatusCode.OK, ""); });
        var inputs = WithHeaders(("Content-Type", value));
        inputs["Method"] = "POST";
        inputs["Body"] = "a,b";

        var act = async () => await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be(
            "Input 'Headers' key 'Content-Type' has a Content-Type with a charset other than utf-8, which is how the block encodes the Body.");
        sent.Should().Be(0);
    }

    [Theory]
    [InlineData("text/csv; charset=UTF-8")]
    [InlineData("text/csv; charset=\"utf-8\"")]
    [InlineData("text/csv")]
    public async Task A_Content_Type_with_a_utf_8_charset_or_none_is_sent(string value)
    {
        var seen = new List<string?>();
        var block = Sending(req => { seen.Add(req.Content?.Headers.ContentType?.MediaType); return Respond(HttpStatusCode.OK, ""); });
        var inputs = WithHeaders(("Content-Type", value));
        inputs["Method"] = "POST";
        inputs["Body"] = "a,b";

        await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        seen.Should().Equal("text/csv");
    }

    public static TheoryData<object?> UnreadableThrowOnFailure => new()
    {
        1, 0.5, null, System.Text.Json.JsonDocument.Parse("null").RootElement.Clone(), new Dictionary<string, object?> { ["on"] = true },
        System.Text.Json.JsonDocument.Parse("{\"on\":true}").RootElement.Clone(),
        "true", " FALSE ", "1", "",
    };

    [Theory]
    [MemberData(nameof(UnreadableThrowOnFailure))]
    public async Task A_throw_on_failure_it_cannot_read_fails_the_node_naming_the_port_before_sending(object? value)
    {
        var sent = 0;
        var block = Sending(_ => { sent++; return Respond(HttpStatusCode.OK, ""); });
        var inputs = Inputs("https://example.com/api");
        inputs["ThrowOnFailure"] = value;

        var act = async () => await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be("Input 'ThrowOnFailure' cannot be read as Boolean.");
        sent.Should().Be(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_readable_throw_on_failure_decides_whether_a_failed_request_throws(object value, bool throws)
    {
        var block = Sending(_ => Respond(HttpStatusCode.InternalServerError, "boom"));
        var inputs = Inputs("https://example.com/api");
        inputs["ThrowOnFailure"] = value;

        var act = async () => await block.ExecuteAsync(inputs, Context(), CancellationToken.None);

        if (throws) await act.Should().ThrowAsync<HttpRequestException>();
        else (await act()).Should().ContainKey("Success").WhoseValue.Should().Be(false);
    }

    [Fact]
    public async Task A_bound_Boolean_from_a_conversion_block_controls_HttpRequest()
    {
        var blocks = Standard(new CapturingFactory(_ => Respond(HttpStatusCode.InternalServerError, "boom")));
        var definition = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition { Id = "parse", BlockTypeId = "StringToBool", Config = new() { ["Value"] = System.Text.Json.JsonSerializer.SerializeToElement("true") } },
                new WorkflowNodeDefinition { Id = "http", BlockTypeId = "HttpRequest", Config = new() { ["Url"] = System.Text.Json.JsonSerializer.SerializeToElement("https://example.com/api") } },
            ],
            Edges =
            [
                new() { Id = "start-parse", SourceNodeId = "start", TargetNodeId = "parse", Mappings = System.Text.Json.JsonSerializer.SerializeToElement(new { }) },
                new() { Id = "parse-http", SourceNodeId = "parse", TargetNodeId = "http", Mappings = System.Text.Json.JsonSerializer.SerializeToElement(new { ThrowOnFailure = new { type = "direct", sourceOutputName = "Result" } }) },
            ],
        };
        var run = async () => await CompiledGraph.RunAsync(definition, blocks);
        (await run.Should().ThrowAsync<WorkflowNodeExecutionException>()).Which.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task An_absent_throw_on_failure_is_the_documented_false()
    {
        var block = Sending(_ => Respond(HttpStatusCode.InternalServerError, "boom"));

        (await block.ExecuteAsync(Inputs("https://example.com/api"), Context(), CancellationToken.None))["Success"].Should().Be(false);
    }

    private static IReadOnlyDictionary<string, object?> MergedWithConfiguredTrue(bool wireProducesNull)
    {
        var wired = new Dictionary<string, object?> { ["Url"] = "https://example.com/api" };
        if (wireProducesNull) wired["ThrowOnFailure"] = null;
        var config = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["ThrowOnFailure"] = System.Text.Json.JsonDocument.Parse("true").RootElement.Clone(),
        };
        var node = new ReeveWorkflow.Core.Compilation.CompiledNode(
            "http", null!, new Dictionary<string, object?> { ["ThrowOnFailure"] = true },
            new Dictionary<string, ReeveWorkflow.Core.Compilation.CompiledExpression>(), [], new Dictionary<string, ReeveWorkflow.Core.Compilation.CanvasPath>(),
            null!, null!, [], []);
        return ReeveWorkflow.Core.Execution.WorkflowGraphExecutor.MergeConfigIntoInputs(wired, config, node);
    }

    [Fact]
    public async Task A_wire_that_produced_null_does_not_hide_a_configured_true_as_false()
    {
        var block = Sending(_ => Respond(HttpStatusCode.InternalServerError, "boom"));
        var merged = MergedWithConfiguredTrue(wireProducesNull: true);

        merged["ThrowOnFailure"].Should().BeNull();
        var act = async () => await block.ExecuteAsync(merged, Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be("Input 'ThrowOnFailure' cannot be read as Boolean.");
    }

    [Fact]
    public async Task A_wire_that_produced_nothing_lets_the_configured_true_throw_on_a_failed_request()
    {
        var block = Sending(_ => Respond(HttpStatusCode.InternalServerError, "boom"));

        var act = async () => await block.ExecuteAsync(MergedWithConfiguredTrue(wireProducesNull: false), Context(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task A_url_the_host_refuses_never_reaches_the_network()
    {
        var sent = 0;
        var block = new GuardingBlock(_ => { sent++; return Respond(HttpStatusCode.OK, ""); });

        var result = await block.ExecuteAsync(Inputs("https://forbidden.example/api"), Context(), CancellationToken.None);

        sent.Should().Be(0, "a guard that runs after the request has gone out guards nothing");
        result["Success"].Should().Be(false);
        result["StatusCode"].Should().Be(0L);
        ((string?)result["Body"]).Should().Contain("blocked");
    }

    [Fact]
    public async Task A_refused_url_throws_when_the_node_asked_for_throw_on_failure()
    {
        var block = new GuardingBlock(_ => Respond(HttpStatusCode.OK, ""));

        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://forbidden.example/api", ["ThrowOnFailure"] = true },
            Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("blocked");
    }

    [Fact]
    public async Task A_host_whose_guard_fires_at_connect_time_still_gets_the_blocked_shape()
    {
        var block = new HostGuardBlock(_ => throw new HostBlockedException("egress to 127.0.0.1 refused"));

        var result = await block.ExecuteAsync(Inputs("http://127.0.0.1/admin"), Context(), CancellationToken.None);

        result["StatusCode"].Should().Be(0L);
        ((string?)result["Body"]).Should().Contain("Request blocked").And.Contain("127.0.0.1");
    }

    [Fact]
    public async Task A_client_the_block_owns_is_disposed_and_one_it_borrows_is_not()
    {
        var owned = new TrackingHandler(_ => Respond(HttpStatusCode.OK, ""));
        var borrowed = new TrackingHandler(_ => Respond(HttpStatusCode.OK, ""));

        await new LeasingBlock(new HttpClient(owned), ownsClient: true)
            .ExecuteAsync(Inputs("https://example.com/a"), Context(), CancellationToken.None);
        var borrowedClient = new HttpClient(borrowed);
        await new LeasingBlock(borrowedClient, ownsClient: false)
            .ExecuteAsync(Inputs("https://example.com/b"), Context(), CancellationToken.None);

        owned.Disposed.Should().BeTrue("the block created that client for one call and nobody else can free it");
        borrowed.Disposed.Should().BeFalse("disposing a pooled client would break every later request through it");
        borrowedClient.Dispose();
    }

    [Fact]
    public void Headers_is_an_optional_map_of_text_and_no_configuration_field()
    {
        var factory = new HttpRequestBlockFactory();

        var headers = factory.Inputs.Single(p => p.Name == "Headers");

        (headers.Type.Name, headers.DisplayName, headers.IsRequired).Should().Be(("Map<String,String>", "Headers", false));
        factory.ConfigurationSchema.Select(f => f.Name).Should().NotContain("Headers");
    }

    public static TheoryData<string, bool> HeadersConfigurationValues => new()
    {
        { """{"X-Key":"k","Accept":"*/*"}""", false },
        { "{}", false },
        { """["X-Key: k", "X-Other: v"]""", true },
        { "[]", true },
        { """{"X-Key":5}""", true },
        { """{"X-Key":["k"]}""", true },
        { "5", true },
    };

    [Theory]
    [MemberData(nameof(HeadersConfigurationValues))]
    public void A_configuration_value_on_Headers_compiles_only_as_an_object_of_text(string stored, bool refused)
    {
        var definition = OneRequest(JsonDocument.Parse(stored).RootElement.Clone());

        var diagnostics = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(Standard()), CanvasSignature.Empty).Diagnostics
            .Select(d => $"{d.RuleId}@{d.NodeId}.{d.PortName}");

        diagnostics.Should().Equal(refused ? ["literal-port-value-invalid@http.Headers"] : []);
    }

    [Fact]
    public void An_8_0_field_line_literal_is_refused_and_never_read_as_a_map()
    {
        var definition = OneRequest(JsonSerializer.SerializeToElement(new[] { "Authorization: Bearer x" }));

        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(Standard()), CanvasSignature.Empty);

        result.Diagnostics.Select(d => $"{d.RuleId}@{d.NodeId}.{d.PortName}").Should().Equal("literal-port-value-invalid@http.Headers");
        result.Program.Should().BeNull();
    }

    [Theory]
    [InlineData(false, "port-type-mismatch")]
    [InlineData(true, "expression-result-type-mismatch")]
    public void A_Collection_of_String_into_Headers_is_refused_as_a_type_mismatch(bool byExpression, string rule)
    {
        var blocks = Standard(extra: new ListSource());
        var http = new WorkflowNodeDefinition
        {
            Id = "http", BlockTypeId = "HttpRequest",
            Config = new() { ["Url"] = JsonSerializer.SerializeToElement("https://example.com/api") },
        };
        object edgeMappings = byExpression ? new { } : new { Headers = new { type = "direct", sourceOutputName = "Lines" } };
        if (byExpression)
            http.Config["Headers"] = JsonSerializer.SerializeToElement("expr:${previous.src.Lines}");
        var definition = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition { Id = "src", BlockTypeId = ListSource.Id },
                http,
            ],
            Edges =
            [
                new() { Id = "e0", SourceNodeId = "start", TargetNodeId = "src", Mappings = JsonSerializer.SerializeToElement(new { }) },
                new() { Id = "e1", SourceNodeId = "src", TargetNodeId = "http", Mappings = JsonSerializer.SerializeToElement(edgeMappings) },
            ],
        };

        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty);

        result.Diagnostics.Select(d => $"{d.RuleId}@{d.NodeId}.{d.PortName}").Should().Equal($"{rule}@http.Headers");
    }

    [Fact]
    public async Task A_configuration_value_on_Headers_reaches_the_request_as_its_fields()
    {
        var seen = new List<HttpRequestMessage>();
        var blocks = Standard(new CapturingFactory(req => { seen.Add(req); return Respond(HttpStatusCode.OK, ""); }));
        var definition = OneRequest(JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["X-Key"] = "k", ["Accept"] = "text/plain" }));

        await RunAsync(definition, blocks);

        var sent = seen.Should().ContainSingle().Which.Headers;
        sent.GetValues("X-Key").Should().Equal("k");
        sent.GetValues("Accept").Should().Equal("text/plain");
    }

    [Fact]
    public async Task A_recorded_node_input_masks_Authorization_and_X_Api_Key_and_keeps_Accept()
    {
        var blocks = Standard(new CapturingFactory(_ => Respond(HttpStatusCode.OK, "")));
        var definition = OneRequest(JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer s3cr3t", ["X-Api-Key"] = "k3y", ["Accept"] = "text/plain",
        }));
        var steps = new List<WorkflowExecutionStep>();

        await RunAsync(definition, blocks, steps);

        var recorded = JsonNode.Parse(steps.Single(step => step.NodeId == "http").InputsJson!)!["Headers"]!.AsObject();
        recorded["Authorization"]!.GetValue<string>().Should().Be("***");
        recorded["X-Api-Key"]!.GetValue<string>().Should().Be("***");
        recorded["Accept"]!.GetValue<string>().Should().Be("text/plain");
    }

    [Fact]
    public async Task Two_keys_of_a_configuration_value_naming_one_field_fail_the_node_and_send_nothing()
    {
        var sent = 0;
        var blocks = Standard(new CapturingFactory(_ => { sent++; return Respond(HttpStatusCode.OK, ""); }));
        var definition = OneRequest(JsonDocument.Parse("""{"Accept":"a","accept":"b"}""").RootElement.Clone());

        var act = async () => await RunAsync(definition, blocks);

        (await act.Should().ThrowAsync<WorkflowNodeExecutionException>()).Which.InnerException!.Message.Should()
            .Be("Input 'Headers' keys 'Accept' and 'accept' name one field; a field is sent once or refused.");
        sent.Should().Be(0);
    }

    private static Task RunAsync(WorkflowDefinition definition, RegisteredWorkflowBlocks blocks, List<WorkflowExecutionStep>? steps = null)
    {
        var compiled = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty);
        compiled.Diagnostics.Should().BeEmpty();

        return new WorkflowGraphExecutor(blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance,
                NullHelperResolver.Instance, new WorkflowGraphExecutorOptions())
            .ExecuteGraphAsync(new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["start"] = new Dictionary<string, object?>() },
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new EmptyServices(), [], steps ?? [], WorkflowTraceMode.Full,
                debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
                inputParametersForRun: new Dictionary<string, object?>(), triggerIdForRun: null, triggerEnvelopeForRun: null,
                cancellationToken: CancellationToken.None, debugHook: null, debugCounter: null, stateExecutionId: null,
                mayParkSleep: false, parameterSignature: null, compiled: CompiledWalk.Root(compiled.Program!));
    }

    private sealed class ListSource : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public const string Id = "ListSource";
        public string BlockTypeId => Id;
        public string DisplayName => Id;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [new("Lines", "Lines", PortValueType.CollectionOf(PortValueType.String), false)];
        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["Lines"] = new List<string> { "X-Key: k" } });
    }

    private static WorkflowDefinition OneRequest(JsonElement headers) => new()
    {
        Nodes =
        [
            new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
            new WorkflowNodeDefinition
            {
                Id = "http", BlockTypeId = "HttpRequest",
                Config = new() { ["Url"] = JsonSerializer.SerializeToElement("https://example.com/api"), ["Headers"] = headers },
            },
        ],
        Edges = [new() { Id = "e0", SourceNodeId = "start", TargetNodeId = "http", Mappings = JsonSerializer.SerializeToElement(new { }) }],
    };

    [Fact]
    public async Task Reads_a_body_as_long_as_the_cap()
    {
        var block = new CappedBlock(64, _ => Task.FromResult(Streaming(HttpStatusCode.OK, new EndlessStream(limit: 64))));

        var result = await block.ExecuteAsync(Inputs("https://example.com/file"), Context(), CancellationToken.None);

        result["Success"].Should().Be(true);
        result["Body"].Should().Be(new string('a', 64));
    }

    [Fact]
    public async Task Fails_the_request_once_a_body_of_no_declared_length_passes_the_cap()
    {
        var stream = new EndlessStream(limit: null);
        var block = new CappedBlock(64, _ => Task.FromResult(Streaming(HttpStatusCode.OK, stream)));

        var result = await block.ExecuteAsync(Inputs("https://example.com/stream"), Context(), CancellationToken.None);

        result["Success"].Should().Be(false);
        result["StatusCode"].Should().Be(0L);
        result["Body"].Should().Be("The response body is larger than 64 bytes, the most the block reads.");
        stream.Served.Should().BeLessThan(64 + 81920 + 1);
    }

    [Fact]
    public async Task Reports_a_connection_lost_while_the_body_is_read_on_its_ports()
    {
        var block = Sending(_ => Task.FromResult(Streaming(HttpStatusCode.OK, new BrokenStream())));

        var result = await block.ExecuteAsync(Inputs("https://example.com/file"), Context(), CancellationToken.None);

        result["Success"].Should().Be(false);
        result["StatusCode"].Should().Be(0L);
        result["Body"].Should().Be("The response body could not be read: the peer reset the connection.");
    }

    [Fact]
    public async Task Reads_a_body_whose_charset_it_does_not_know_as_utf_8()
    {
        var block = Sending(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("zażółć"u8.ToArray()) };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain; charset=no-such-charset");
            return Task.FromResult(response);
        });

        var result = await block.ExecuteAsync(Inputs("https://example.com/text"), Context(), CancellationToken.None);

        result["Success"].Should().Be(true);
        result["StatusCode"].Should().Be(200L);
        result["Body"].Should().Be("zażółć");
    }

    [Fact]
    public async Task Refuses_a_declared_length_over_the_default_cap_without_reading_it()
    {
        var stream = new EndlessStream(limit: null);
        var block = Sending(_ =>
        {
            var response = Streaming(HttpStatusCode.OK, stream);
            response.Content.Headers.ContentLength = 5L * 1024 * 1024;
            return Task.FromResult(response);
        });

        var act = () => block.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://example.com/big", ["ThrowOnFailure"] = true },
            Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should()
            .Be("The response body is larger than 4194304 bytes, the most the block reads.");
        stream.Served.Should().Be(0);
    }

    [Fact]
    public async Task Quotes_at_most_the_start_of_a_failed_response_body_in_the_failure()
    {
        var body = new string('x', 5000);
        var block = Sending(_ => Respond(HttpStatusCode.InternalServerError, body));

        var act = () => block.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://example.com/api", ["ThrowOnFailure"] = true },
            Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should()
            .Be($"HTTP request failed with status 500: {new string('x', 1024)}... (5000 characters)");
    }

    private static RegisteredWorkflowBlocks Standard(HttpRequestBlockFactory? http = null, IWorkflowBlockFactory? extra = null)
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        var factories = services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>()
            .Where(f => http is null || f.BlockTypeId != "HttpRequest");
        var all = http is null ? factories : factories.Append(http);
        return new RegisteredWorkflowBlocks(extra is null ? all : all.Append(extra));
    }

    private sealed class CapturingFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpRequestBlockFactory
    {
        public override IWorkflowBlockPlugin Create() => new StubBlock(handler);
    }

    private static readonly HttpRequestOptionsKey<CancellationToken> SendToken = new("send-token");

    private static Dictionary<string, object?> Inputs(string url) => new() { ["Url"] = url };

    private static Dictionary<string, object?> WithHeaders(params (string Name, string Value)[] fields) =>
        new()
        {
            ["Url"] = "https://example.com/api",
            ["Headers"] = fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal),
        };

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    private static StubBlock Sending(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => new(handler);

    private static WorkflowBlockContext Context() => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = NullLogger.Instance,
        Services = new EmptyServices()
    };

    private class StubBlock(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpRequestBlock
    {
        protected override HttpClientLease ResolveHttpClient(WorkflowBlockContext context) =>
            new(new HttpClient(new TrackingHandler(handler)), CallerOwnsClient: true);
    }

    private sealed class CappedBlock(long maxBytes, Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : StubBlock(handler)
    {
        protected override long MaxResponseBodyBytes => maxBytes;
    }

    private static HttpResponseMessage Streaming(HttpStatusCode status, Stream body) =>
        new(status) { Content = new StreamContent(body) };

    private sealed class BrokenStream : Stream
    {
        private bool served;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (served)
                throw new IOException("the peer reset the connection.");
            served = true;
            buffer[offset] = (byte)'a';
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class EndlessStream(long? limit) : Stream
    {
        public long Served { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Served >= (limit ?? 16L * 1024 * 1024))
                return limit is null ? throw new InvalidOperationException("The block read past its cap.") : 0;
            var n = (int)Math.Min(count, (limit ?? long.MaxValue) - Served);
            buffer.AsSpan(offset, n).Fill((byte)'a');
            Served += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class GuardingBlock(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : StubBlock(handler)
    {
        protected override void ValidateUrl(string url, WorkflowBlockContext context) =>
            throw new WorkflowEgressBlockedException($"host 'forbidden.example' is not allowed");
    }

    private sealed class HostGuardBlock(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : StubBlock(handler)
    {
        protected override bool IsEgressBlocked(Exception exception) => exception is HostBlockedException;
    }

    private sealed class LeasingBlock(HttpClient client, bool ownsClient) : HttpRequestBlock
    {
        protected override HttpClientLease ResolveHttpClient(WorkflowBlockContext context) => new(client, ownsClient);
    }

    private sealed class HostBlockedException(string message) : Exception(message);

    private sealed class TrackingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Options.Set(SendToken, cancellationToken);
            return handler(request);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
