using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Dispatch.Host.Tests;

public sealed class DispatchReplica : IAsyncDisposable
{
    private static readonly string EntryPoint =
        typeof(DispatchReplica).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "DispatchHostEntryPoint")
            .Value!;

    private readonly Process process;
    private readonly StringBuilder console = new();

    private DispatchReplica(string id, Process process, HttpClient client)
    {
        Id = id;
        this.process = process;
        Client = client;
    }

    public string Id { get; }

    public HttpClient Client { get; }

    public string Console
    {
        get { lock (console) return console.ToString(); }
    }

    public const string RefusedToStart = "Defaults.IsMultiReplica is true";

    public bool HasExited => process.HasExited;

    public static async Task<DispatchReplica> StartAsync(DispatchReplicaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!File.Exists(EntryPoint))
            throw new InvalidOperationException($"The example host was not built at '{EntryPoint}'.");

        for (var attempt = 1; ; attempt++)
        {
            var replica = Spawn(settings, FreePort());
            try
            {
                await replica.WaitUntilServingAsync();
                return replica;
            }
            catch (InvalidOperationException) when (attempt < 3 && !replica.Console.Contains(RefusedToStart))
            {
                await replica.DisposeAsync();
            }
        }
    }

    private static DispatchReplica Spawn(DispatchReplicaSettings settings, int port)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(EntryPoint);
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["DISPATCH_POSTGRES"] = settings.PostgresConnectionString;
        if (settings.RedisConnectionString is { } redis)
            startInfo.Environment["DISPATCH_REDIS"] = redis;
        startInfo.Environment["DISPATCH_REPLICA_ID"] = settings.ReplicaId;
        startInfo.Environment["DISPATCH_HAS_PEER_REPLICAS"] =
            settings.HasPeerReplicas ? "true" : "false";
        startInfo.Environment["DISPATCH_ABANDONED_RUN_AFTER_SECONDS"] =
            settings.AbandonedRunAfter.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
        if (settings.StaticRoot is { } staticRoot)
            startInfo.Environment[Dispatch.Host.TestHooks.StaticPageHost.StaticRoot] = staticRoot;
        if (settings.StallAfterTakingAWait)
            startInfo.Environment[Dispatch.Host.TestHooks.FaultHooks.StallAfterTakingAWait] = "true";
        if (settings.RestartWordUnheard)
            startInfo.Environment[Dispatch.Host.TestHooks.FaultHooks.RestartWordUnheard] = "true";
        if (settings.EventOnlyFireKeys)
            startInfo.Environment[Dispatch.Host.TestHooks.FaultHooks.EventOnlyFireKeys] = "true";
        startInfo.Environment["DISPATCH_RUN_HEARTBEAT_SECONDS"] =
            settings.RunHeartbeatInterval.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
        startInfo.Environment["DISPATCH_WAKER_POLL_SECONDS"] =
            settings.WakerPollInterval.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["DISPATCH_RECOVERY_SWEEP_SECONDS"] =
            settings.RecoverySweepInterval.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
        startInfo.Environment["DISPATCH_SHUTDOWN_TIMEOUT_SECONDS"] =
            settings.ShutdownTimeout.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The example host process did not start.");

        var replica = new DispatchReplica(
            settings.ReplicaId,
            process,
            new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") });

        process.OutputDataReceived += replica.Capture;
        process.ErrorDataReceived += replica.Capture;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return replica;
    }

    public Task<DispatchResponse> ExecuteAsync(Guid workflowId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/workflows/{workflowId}/execute")
        {
            Content = JsonContent.Create(new { debugMode = false })
        });

    public async Task GrantClearanceAsync(string shipment)
    {
        var response = await Client.PostAsJsonAsync("api/depot-clearances", new { shipment });
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            throw new InvalidOperationException(
                $"Replica '{Id}' answered {(int)response.StatusCode} to a clearance for '{shipment}'."
                + Environment.NewLine + await response.Content.ReadAsStringAsync());
        }
    }

    public Task<DispatchResponse> ObserveDepotEventAsync(Guid workflowId, string eventId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/depot-events")
        {
            Content = JsonContent.Create(new
            {
                workflowId,
                eventId,
                observedAtUtc = DateTimeOffset.UtcNow
            })
        });

    public async Task<EditorResponse> EditAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add(DispatchHost.OperatorHeader, FreightDesk.Operator.ToString());

        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new EditorResponse(response.StatusCode, response.Headers.Location?.ToString(), text);
    }

    public void Kill()
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);

        process.WaitForExit(TimeSpan.FromSeconds(30));
    }

    public async Task<int> StopAsync(TimeSpan patience)
    {
        if (!process.HasExited && Terminate(process.Id, SignalTerminate) != 0)
            throw new InvalidOperationException($"SIGTERM to replica {Id} failed.");

        using var timeout = new CancellationTokenSource(patience);
        await process.WaitForExitAsync(timeout.Token);
        return process.ExitCode;
    }

    private const int SignalTerminate = 15;

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Terminate(int pid, int signal);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        try { Kill(); }
        catch (InvalidOperationException) { }

        await Task.Yield();
        process.Dispose();
    }

    private async Task<DispatchResponse> SendAsync(HttpRequestMessage request)
    {
        using (request)
        {
            request.Headers.Add(DispatchHost.OperatorHeader, FreightDesk.Operator.ToString());
            using var response = await Client.SendAsync(request);
            var outputs = response.Content.Headers.ContentLength is > 0
                ? await response.Content.ReadFromJsonAsync<Dictionary<string, object?>>()
                : null;
            return new DispatchResponse(response.StatusCode, outputs ?? []);
        }
    }

    private void Capture(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null)
        {
            lock (console)
                console.AppendLine(args.Data);
        }
    }

    private async Task WaitUntilServingAsync()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                process.WaitForExit();
                throw new InvalidOperationException($"Replica '{Id}' exited before serving.{Environment.NewLine}{Console}");
            }

            try
            {
                using var response = await Client.GetAsync("/health");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"Replica '{Id}' never became ready.{Environment.NewLine}{Console}");
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

public sealed record DispatchReplicaSettings
{
    public required string ReplicaId { get; init; }

    public required string PostgresConnectionString { get; init; }

    public string? RedisConnectionString { get; init; }

    public bool HasPeerReplicas { get; init; } = true;

    public TimeSpan AbandonedRunAfter { get; init; } = TimeSpan.FromMinutes(1);

    public string? StaticRoot { get; init; }

    public bool StallAfterTakingAWait { get; init; }

    public bool RestartWordUnheard { get; init; }

    public bool EventOnlyFireKeys { get; init; }

    public TimeSpan RunHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan RecoverySweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan WakerPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record EditorResponse(HttpStatusCode StatusCode, string? Location, string Body)
{
    public System.Text.Json.JsonElement Json =>
        System.Text.Json.JsonDocument.Parse(Body).RootElement;
}

public sealed record DispatchResponse(HttpStatusCode Status, IReadOnlyDictionary<string, object?> Outputs);
