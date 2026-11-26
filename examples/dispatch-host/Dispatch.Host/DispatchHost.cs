using System.Security.Claims;
using Dispatch.Host.Engine;
using Dispatch.Host.Freight;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.AspNetCore;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Editor;
using ReeveWorkflow.Npgsql;
using ReeveWorkflow.Redis;
using StackExchange.Redis;

namespace Dispatch.Host;

public static class DispatchHost
{
    public const string OperatorHeader = "X-Dispatch-Operator";

    public static WebApplication Build(DispatchHostOptions options, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = options.ShutdownTimeout);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IWorkflowBlockFactory, AssignCarrierBlockFactory>();
        builder.Services.AddSingleton<IWorkflowBlockFactory, PrintLabelBlockFactory>();
        builder.Services.AddSingleton<IWorkflowBlockFactory, HoldUntilBlockFactory>();
        builder.Services.AddStandardWorkflowBlocks();
        builder.Services.AddHostedService<WorkflowSchemaMigrationService>();
        builder.Services.AddDataProtection().SetApplicationName("dispatch-host")
            .AddKeyManagementOptions(keys => keys.XmlRepository = new PostgresKeyRing(options.PostgresConnectionString));
        builder.Services.AddSingleton<IWorkflowSecretCodec, DataProtectionSecretCodec>();
        if (options.RedisConnectionString is { } redisConnectionString)
        {
            builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));
            builder.Services.AddSingleton<ClearanceDesk>();
            builder.Services.AddSingleton<IWorkflowBlockFactory, AwaitClearanceBlockFactory>();
        }
        builder.Services.AddWorkflows(workflows =>
        {
            TestHooks.StaticPageHost.RegisterExpressionChecksWhenServingPage(workflows.Seams);
            workflows.Seams.PortTypes.Add(FreightPorts.ShipmentRecord);
            workflows.ConfigureDbContext(context => context.UseNpgsql(options.PostgresConnectionString));
            workflows.Defaults.ReplicaId = options.ReplicaId;
            workflows.Defaults.IsMultiReplica = options.HasPeerReplicas;
            workflows.Defaults.SignalChannels.RestartCancel = RestartCancelChannel;
            if (options.RedisConnectionString is not null)
            {
                workflows.Defaults.ExpressionNamespaces.Add(new WorkflowExpressionNamespace(
                    "depot", 2, new ClearanceStaticCheck(),
                    async (scope, cancellationToken) =>
                        await scope.Services.GetRequiredService<ClearanceDesk>().ReadAsync(cancellationToken))
                {
                    AnswerVersion = 1,
                });
                workflows.Seams.SuspensionWakeSource.Use<ClearanceWakeSource>();
            }
            workflows.Seams.RunLockProvider.UseNpgsqlLocks(locks =>
                locks.ConnectionString = options.PostgresConnectionString);
            if (options.RedisConnectionString is not null)
            {
                workflows.Seams.RunSlotGate.UseRedis(redis => redis.RunSlotKeyPrefix = RunSlotKeyPrefix);
                workflows.Seams.FireDedup.UseRedis(redis => redis.KeyPrefix = FireDedupKeyPrefix);
                workflows.Seams.ClusterSignal.UseRedis();
            }
        })
        .AddPostgreSqlSchema()
        .AddWorkflowEditor()
        .AddWorkflowEngine(engine =>
        {
            engine.Recovery.Enabled = true;
            engine.Recovery.AbandonedAfter = options.AbandonedRunAfter;
            engine.RunHeartbeatInterval = options.RunHeartbeatInterval;
            engine.Recovery.SweepInterval = options.RecoverySweepInterval;
            engine.Recovery.BatchSize = options.RecoveryBatchSize;
            engine.Waker = engine.Waker with { PollInterval = options.WakerPollInterval };
        });
        TestHooks.StaticPageHost.RegisterTestBlocksWhenServingPage(builder.Services);
        TestHooks.StaticPageHost.EnableFullTraceWhenServingPage(builder.Services);
        TestHooks.FaultHooks.Apply(builder.Services);

        var app = builder.Build();
        TestHooks.StaticPageHost.Serve(app);

        app.Use(ReadOperatorFromHeader);
        app.MapGet("/health", () => Results.Ok(new HealthResponse(options.ReplicaId)));

        app.MapWorkflowApi("api/workflows").AllowAnonymous();

        app.MapWorkflowEditorApi("api/workflows").AllowAnonymous();

        app.MapDepotFeed("api/depot-events").AllowAnonymous();
        if (options.RedisConnectionString is not null)
            app.MapClearanceDesk("api/depot-clearances").AllowAnonymous();

        app.MapWorkflowBlockApi("api/workflow-blocks").AllowAnonymous();
        app.MapWorkflowExpressionApi("api/expressions").AllowAnonymous();

        return app;
    }

    public const string RunSlotKeyPrefix = "dispatch:run-slots:";

    public const string FireDedupKeyPrefix = "dispatch:fire-dedup:";

    public const string RestartCancelChannel = "dispatch:workflow-restart-cancel";

    private static Task ReadOperatorFromHeader(HttpContext context, RequestDelegate next)
    {
        if (Guid.TryParse(context.Request.Headers[OperatorHeader], out var operatorId))
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, operatorId.ToString())], "dispatch-header"));
        }

        return next(context);
    }

    private sealed record HealthResponse(string Replica);
}
