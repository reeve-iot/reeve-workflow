using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.AspNetCore;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting.Defaults;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class StandardCatalogueWireTests
{
    private static string CommittedPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "packages", "adapters", "src",
            "standard-catalogue.wire.json"));

    [Fact]
    public async Task The_committed_wire_catalogue_is_what_the_endpoint_writes_for_every_standard_block()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.Services.AddStandardWorkflowBlocks();
        builder.Services.AddSingleton<IWorkflowBlocks>(sp =>
            new RegisteredWorkflowBlocks(sp.GetServices<IWorkflowBlockFactory>()));
        await using var app = builder.Build();
        app.MapWorkflowBlockApi("api/workflow-blocks").AllowAnonymous();
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var expected = await client.GetStringAsync("/api/workflow-blocks");
        await app.StopAsync();

        var path = CommittedPath();
        var actual = File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
        if (actual != expected)
            await File.WriteAllTextAsync(path + ".actual", expected);

        actual.Should().Be(expected, because: $"the endpoint's response is at {path}.actual");
        expected.Should().Contain("\"allowedValues\":null", because: "the fixture must carry the nulls the wire sends");
    }
}
