using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StackExchange.Redis;

namespace Test.Monica.Locker.Redis.Support;

/// <summary>Owns six Redis processes in one disposable container, reachable through loopback only.</summary>
internal sealed class RedisSentinelSandbox : IAsyncDisposable
{
    private const string OWNER_LABEL = "monica.test.sentinel";
    private const string ROOT = "/tmp/monica-lease-test";
    private bool _created;

    internal string Owner { get; } = Guid.NewGuid().ToString("N");
    internal string ContainerName => "monica-lease-sentinel-" + Owner;
    internal string ServiceName => "monica-leases-" + Owner;
    internal int[] RedisPorts { get; private set; } = [];
    internal int[] SentinelPorts { get; private set; } = [];
    internal string ImageId { get; private set; } = string.Empty;
    internal bool Removed { get; private set; }

    internal static async Task<RedisSentinelSandbox> StartAsync(CancellationToken token)
    {
        var sandbox = new RedisSentinelSandbox();
        try
        {
            var image = Environment.GetEnvironmentVariable("MONICA_LEASE_TEST_REDIS_IMAGE") ?? "redis:7-alpine";
            sandbox.ImageId = (await DockerAsync(["image", "inspect", "--format", "{{.Id}}", image], token)).Trim();
            var reservations = Enumerable.Range(0, 6).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
            int[] ports;
            try
            {
                foreach (var listener in reservations) listener.Start();
                ports = reservations.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port).ToArray();
            }
            finally
            {
                foreach (var listener in reservations) listener.Stop();
            }
            sandbox.RedisPorts = ports[..3];
            sandbox.SentinelPorts = ports[3..];
            List<string> arguments = ["run", "--detach", "--pull", "never", "--name", sandbox.ContainerName,
                "--label", OWNER_LABEL + "=" + sandbox.Owner];
            foreach (var port in ports) arguments.AddRange(["--publish", $"127.0.0.1:{port}:{port}"]);
            arguments.AddRange([sandbox.ImageId, "sh", "-c", sandbox.StartupScript()]);
            // A failed/timed-out CLI can still have created the container. Cleanup verifies its ownership label.
            sandbox._created = true;
            await DockerAsync(arguments, token);
            return sandbox;
        }
        catch (Exception startupFailure)
        {
            try { await sandbox.DisposeAsync(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException($"Sentinel startup and cleanup failed for {sandbox.ContainerName}.",
                    startupFailure, cleanupFailure);
            }
            throw;
        }
    }

    internal ConfigurationOptions SentinelOptions()
    {
        var options = DataOptions();
        options.ServiceName = null;
        options.CommandMap = CommandMap.Sentinel;
        options.TieBreaker = string.Empty;
        return options;
    }

    internal ConfigurationOptions DataOptions()
    {
        var options = new ConfigurationOptions
        {
            ServiceName = ServiceName, AllowAdmin = true, AbortOnConnectFail = false,
            ConnectTimeout = 1500, ConnectRetry = 1, AsyncTimeout = 1000, SyncTimeout = 1000,
            BacklogPolicy = BacklogPolicy.FailFast, ReconnectRetryPolicy = new LinearRetry(100)
        };
        foreach (var port in SentinelPorts) options.EndPoints.Add(IPAddress.Loopback, port);
        return options;
    }

    internal async Task StopPrimaryAsync(EndPoint endpoint, CancellationToken token)
    {
        var port = endpoint switch
        {
            IPEndPoint ip when IPAddress.IsLoopback(ip.Address) => ip.Port,
            DnsEndPoint dns when dns.Host == "127.0.0.1" => dns.Port,
            _ => throw new InvalidOperationException("The Sentinel primary is outside the owned loopback topology.")
        };
        if (!RedisPorts.Contains(port)) throw new InvalidOperationException("The primary port is not owned by this scenario.");
        await VerifyOwnerAsync(token);
        await DockerAsync(["exec", ContainerName, "sh", "-c", $"kill -KILL \"$(cat {ROOT}/redis-{port}.pid)\""], token);
    }

    private string StartupScript()
    {
        var script = new StringBuilder($"set -eu\nmkdir -p {ROOT}\n");
        for (var index = 0; index < RedisPorts.Length; index++)
        {
            var port = RedisPorts[index];
            script.Append($"mkdir -p {ROOT}/redis-{port}\n" +
                $"redis-server --port {port} --bind 0.0.0.0 --protected-mode no --save '' --appendonly no " +
                $"--dir {ROOT}/redis-{port} --daemonize yes --pidfile {ROOT}/redis-{port}.pid --logfile {ROOT}/redis-{port}.log " +
                "--repl-diskless-sync-delay 0 ");
            if (index > 0) script.Append($"--replicaof 127.0.0.1 {RedisPorts[0]} --replica-announce-ip 127.0.0.1 --replica-announce-port {port} ");
            script.Append('\n');
        }
        foreach (var port in SentinelPorts)
        {
            script.Append($"cat > {ROOT}/sentinel-{port}.conf <<'CONFIG'\n" +
                $"port {port}\nbind 0.0.0.0\nprotected-mode no\ndir {ROOT}\n" +
                $"pidfile {ROOT}/sentinel-{port}.pid\nlogfile {ROOT}/sentinel-{port}.log\ndaemonize yes\n" +
                $"sentinel monitor {ServiceName} 127.0.0.1 {RedisPorts[0]} 2\n" +
                $"sentinel down-after-milliseconds {ServiceName} 1500\n" +
                $"sentinel failover-timeout {ServiceName} 5000\n" +
                $"sentinel parallel-syncs {ServiceName} 1\n" +
                $"sentinel announce-ip 127.0.0.1\nsentinel announce-port {port}\nCONFIG\n" +
                $"redis-server {ROOT}/sentinel-{port}.conf --sentinel\n");
        }
        // Keeping the container alive lets one Redis process fail without stopping the other five processes.
        script.Append("exec sleep infinity\n");
        return script.ToString();
    }

    private async Task VerifyOwnerAsync(CancellationToken token)
    {
        var owner = (await DockerAsync(["inspect", "--format", "{{ index .Config.Labels \"" + OWNER_LABEL + "\" }}", ContainerName], token)).Trim();
        if (owner != Owner) throw new InvalidOperationException("Refusing to modify a container without this scenario's ownership label.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_created) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        if (await ExistsAsync(deadline.Token))
        {
            await VerifyOwnerAsync(deadline.Token);
            await DockerAsync(["rm", "--force", ContainerName], deadline.Token);
        }
        if (await ExistsAsync(deadline.Token))
            throw new InvalidOperationException($"Owned Sentinel container {ContainerName} remains after cleanup.");
        _created = false;
        Removed = true;
    }

    private async Task<bool> ExistsAsync(CancellationToken token)
    {
        var containers = await DockerAsync(["ps", "--all", "--filter", "name=^/" + ContainerName + "$", "--format", "{{.Names}}"], token);
        return containers.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(ContainerName);
    }

    private static async Task<string> DockerAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker could not start.");
        var streams = Task.WhenAll(process.StandardOutput.ReadToEndAsync(deadline.Token),
            process.StandardError.ReadToEndAsync(deadline.Token));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var output = await streams;
            if (process.ExitCode != 0) throw new InvalidOperationException($"Docker exited with {process.ExitCode}: {output[1]}");
            return output[0];
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            // Observe both redirected streams even when the CLI times out or is cancelled.
            try { await streams; }
            catch (Exception) when (streams.IsCompleted) { }
            throw;
        }
    }
}
