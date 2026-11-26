using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Editor;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.TestHooks;

public static class StaticPageHost
{
    public const string StaticRoot = "DISPATCH_TEST_STATIC_ROOT";

    public static void RegisterTestBlocksWhenServingPage(IServiceCollection services)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StaticRoot))) return;

        services.AddSingleton<IWorkflowBlockFactory, EchoJsonBlockFactory>();
        services.AddSingleton<IWorkflowBlockFactory, RequireLabelBlockFactory>();
        services.AddSingleton<IWorkflowBlockFactory, EnumFieldBlockFactory>();
    }

    public static void RegisterExpressionChecksWhenServingPage(WorkflowHostSeams seams)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StaticRoot))) return;

        seams.ExpressionNamespaces.Add(new WorkflowExpressionNamespace(
            "e2e", 1, new TestNamespaceStaticCheck(),
            (_, _) => Task.FromResult<IExpressionNamespaceResolver>(new TestNamespaceResolver())));
    }

    public static void EnableFullTraceWhenServingPage(IServiceCollection services)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StaticRoot))) return;

        var descriptor = services.Single(d => d.ServiceType == typeof(WorkflowEngineOptions));
        if (descriptor.ImplementationInstance is WorkflowEngineOptions options)
            options.TraceMode = WorkflowTraceMode.Full;
    }

    public static void Serve(WebApplication app)
    {
        var root = Environment.GetEnvironmentVariable(StaticRoot);
        if (string.IsNullOrWhiteSpace(root)) return;

        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
            throw new InvalidOperationException($"{StaticRoot} names {full}, which is not a directory.");

        app.MapGet("/catalogue-refusal/api/workflow-blocks", () => TypedResults.Ok(RefusedCatalogue())).AllowAnonymous();
        app.MapGet("/catalogue-refusal/api/workflows/port-types", ([FromServices] CompilationOptions options, [FromServices] IServiceProvider services) =>
            TypedResults.Ok(PortTypeVocabularyResponse.From(options.Vocabulary, services))).AllowAnonymous();
        app.MapGet("/catalogue-refusal/api/workflows", () => TypedResults.Ok(Array.Empty<WorkflowSummaryResponse>())).AllowAnonymous();

        app.MapGet("/e2e-echo/{name}", (string name, HttpRequest request) => TypedResults.Text(request.Headers[name].ToString())).AllowAnonymous();

        var files = new PhysicalFileProvider(full);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    }

    private static WorkflowBlockTypeResponse[] RefusedCatalogue()
    {
        var port = new BlockPortResponse("Out", "Out", "String", false, null);
        var plain = new WorkflowBlockTypeResponse(
            "unique-catalogue", "Plain block", "", "Catalogue refusals", [],
            [port], [], [],
            new Dictionary<string, IReadOnlyCollection<BlockPortResponse>>(),
            new Dictionary<string, IReadOnlyCollection<BlockPortResponse>>(),
            HasDynamicPorts: false);
        return
        [
            plain,
            plain with
            {
                BlockTypeId = "unparseable-term-catalogue", DisplayName = "Unparseable term",
                Inputs = [port with { Name = "Payload", Type = "Int??" }], Outputs = [],
            },
        ];
    }
}

internal sealed class TestNamespaceResolver : IExpressionNamespaceResolver
{
    public string Namespace => "e2e";
    public ResolveResult Resolve(string[] segments) => segments switch
    {
        ["document"] => ResolveResult.Resolved(JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { ["a"] = new Dictionary<string, object?> { ["b"] = new object?[] { true } } })),
        _ => ResolveResult.Resolved(segments is ["known"]),
    };
}
