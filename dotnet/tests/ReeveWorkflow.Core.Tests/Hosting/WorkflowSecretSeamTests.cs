using System.Text.Json.Nodes;
using FluentAssertions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Hosting;

public class WorkflowSecretSeamTests
{
    public static TheoryData<string, bool> Names => new()
    {
        { "Authorization", true },
        { "authorization", true },
        { "X-Api-Key", true },
        { "x_api_key", true },
        { "X Api Key", true },
        { "private-key", true },
        { "Set-Cookie", true },
        { "client_secret", true },
        { "X-Access-Token", true },
        { "DB_PASSWORD", true },
        { "credentials", true },
        { "Accept", false },
        { "Content-Type", false },
        { "Ocp-Apim-Subscription-Key", false },
        { "X-Hub-Signature", false },
        { "X-Auth", false },
        { "Key", false },
        { "", false },
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void The_rule_names_a_secret_by_a_fragment_ignoring_case_and_separators(string name, bool expected) =>
        WorkflowSensitiveNames.Matches(name).Should().Be(expected);

    [Theory]
    [MemberData(nameof(Names))]
    public void A_recorded_input_masks_a_name_exactly_when_the_rule_matches_it(string name, bool matches)
    {
        if (name.Length == 0)
            return;

        var recorded = JsonNode.Parse(PayloadSanitizer.SerializeAndSanitize(
            new Dictionary<string, object?> { [name] = "plain-value" }, maxValueBytes: 1000)!)!.AsObject();

        recorded.Single().Value!.GetValue<string>().Should().Be(matches ? "***" : "plain-value");
    }

    [Fact]
    public void Two_different_canvases_never_write_the_same_site_text()
    {
        var slashInNode = CanvasPath.Root.Child("a/b", "c");
        var slashInBranch = CanvasPath.Root.Child("a", "b/c");
        var nested = CanvasPath.Root.Child("a", "b").Child("c", "d");

        var texts = new[] { slashInNode, slashInBranch, nested, CanvasPath.Root }.Select(WorkflowSecretSite.CanvasPathText).ToArray();

        texts.Should().OnlyHaveUniqueItems();
        WorkflowSecretSite.CanvasPathText(CanvasPath.Root.Child("a", "b")).Should().Be(WorkflowSecretSite.CanvasPathText(CanvasPath.Root.Child("a", "b")));
        WorkflowSecretSite.CanvasPathText(CanvasPath.Root).Should().BeEmpty();
    }
}
