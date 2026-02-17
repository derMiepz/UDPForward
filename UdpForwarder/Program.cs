using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

return await ProgramEntry.RunAsync(args);

internal static class ProgramEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var configPath = ResolveConfigPath();
            var config = await LoadConfigAsync(configPath);
            var listenAddress = await ResolveAddressAsync(config.ListenHost, preferIPv4: true);
            var targets = await ResolveTargetsAsync(config.Targets);

            using var listener = CreateListener(listenAddress, config.ListenPort);
            using var senderPool = new SenderPool();
            using var cancellationSource = new CancellationTokenSource();

            var stats = new ForwardStats();
            var errorLogGate = new ErrorLogGate(TimeSpan.FromSeconds(5));

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                if (!cancellationSource.IsCancellationRequested)
                {
                    Console.WriteLine("Stopping forwarder...");
                    cancellationSource.Cancel();
                }
            };

            PrintStartup(configPath, listenAddress, config.ListenPort, targets, config.StatsIntervalSeconds);
            var statsTask = PrintStatsAsync(stats, config.StatsIntervalSeconds, cancellationSource.Token);

            try
            {
                while (!cancellationSource.Token.IsCancellationRequested)
                {
                    UdpReceiveResult packet;
                    try
                    {
                        packet = await listener.ReceiveAsync(cancellationSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    stats.RecordInbound(packet.Buffer.Length);

                    foreach (var target in targets)
                    {
                        try
                        {
                            await senderPool.SendAsync(packet.Buffer, target.EndPoint);
                            stats.RecordForward();
                        }
                        catch (Exception ex)
                        {
                            stats.RecordFailure();
                            if (errorLogGate.ShouldLog(target.Label))
                            {
                                Console.Error.WriteLine($"Forward error to {target.Label}: {ex.Message}");
                            }
                        }
                    }
                }
            }
            finally
            {
                cancellationSource.Cancel();
                try
                {
                    await statsTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown.
                }
            }

            Console.WriteLine("Forwarder stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Startup failed: {ex.Message}");
            return 1;
        }
    }

    private static string ResolveConfigPath()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "forwarder.json"));
    }

    private static async Task<ForwarderConfig> LoadConfigAsync(string configPath)
    {
        if (!File.Exists(configPath))
        {
            var defaultConfig = CreateDefaultConfig();
            var defaultJson = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            var configDirectory = Path.GetDirectoryName(configPath);

            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                Directory.CreateDirectory(configDirectory);
            }

            await File.WriteAllTextAsync(configPath, $"{defaultJson}{Environment.NewLine}");
            Console.WriteLine($"Created default config: {configPath}");
        }

        var json = await File.ReadAllTextAsync(configPath);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var config = JsonSerializer.Deserialize<ForwarderConfig>(json, options)
            ?? throw new InvalidDataException("Config file is empty or invalid JSON.");

        config.ListenHost = string.IsNullOrWhiteSpace(config.ListenHost) ? "0.0.0.0" : config.ListenHost.Trim();

        if (config.ListenPort is < 1 or > 65535)
        {
            throw new InvalidDataException("listenPort must be between 1 and 65535.");
        }

        if (config.StatsIntervalSeconds < 0)
        {
            throw new InvalidDataException("statsIntervalSeconds cannot be negative.");
        }

        config.Targets ??= [];
        config.Targets = config.Targets.Where(target => target.Enabled).ToList();

        if (config.Targets.Count == 0)
        {
            throw new InvalidDataException("At least one enabled target is required in the targets list.");
        }

        foreach (var target in config.Targets)
        {
            target.Host = string.IsNullOrWhiteSpace(target.Host) ? "127.0.0.1" : target.Host.Trim();

            if (target.Port is < 1 or > 65535)
            {
                throw new InvalidDataException($"Target '{GetTargetLabel(target)}' has invalid port '{target.Port}'.");
            }
        }

        return config;
    }

    private static ForwarderConfig CreateDefaultConfig()
    {
        return new ForwarderConfig
        {
            ListenHost = "0.0.0.0",
            ListenPort = 30000,
            StatsIntervalSeconds = 5,
            Targets =
            [
                new ForwardTarget
                {
                    Name = "Dash app",
                    Host = "127.0.0.1",
                    Port = 31001,
                    Enabled = true
                },
                new ForwardTarget
                {
                    Name = "Telemetry app",
                    Host = "127.0.0.1",
                    Port = 31002,
                    Enabled = true
                }
            ]
        };
    }

    private static async Task<IPAddress> ResolveAddressAsync(string host, bool preferIPv4)
    {
        if (host == "*")
        {
            return IPAddress.Any;
        }

        if (IPAddress.TryParse(host, out var parsedAddress))
        {
            return parsedAddress;
        }

        var addresses = await Dns.GetHostAddressesAsync(host);
        if (addresses.Length == 0)
        {
            throw new InvalidDataException($"Host '{host}' could not be resolved.");
        }

        if (preferIPv4)
        {
            var ipv4 = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is not null)
            {
                return ipv4;
            }
        }

        var ipv6 = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6);
        return ipv6 ?? addresses[0];
    }

    private static async Task<List<TargetEndpoint>> ResolveTargetsAsync(List<ForwardTarget> configuredTargets)
    {
        var resolved = new List<TargetEndpoint>(configuredTargets.Count);
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in configuredTargets)
        {
            var address = await ResolveAddressAsync(target.Host, preferIPv4: true);
            var endpoint = new IPEndPoint(address, target.Port);
            var dedupeKey = $"{endpoint.Address}:{endpoint.Port}";

            if (!dedupe.Add(dedupeKey))
            {
                Console.WriteLine($"Skipping duplicate target endpoint: {dedupeKey}");
                continue;
            }

            resolved.Add(new TargetEndpoint(GetTargetLabel(target), endpoint));
        }

        if (resolved.Count == 0)
        {
            throw new InvalidDataException("No unique target endpoints were resolved.");
        }

        return resolved;
    }

    private static UdpClient CreateListener(IPAddress listenAddress, int listenPort)
    {
        UdpClient listener = listenAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => new UdpClient(AddressFamily.InterNetwork),
            AddressFamily.InterNetworkV6 => new UdpClient(AddressFamily.InterNetworkV6),
            _ => throw new InvalidOperationException(
                $"Listen address family '{listenAddress.AddressFamily}' is not supported.")
        };

        listener.Client.Bind(new IPEndPoint(listenAddress, listenPort));
        return listener;
    }

    private static Task PrintStatsAsync(ForwardStats stats, int intervalSeconds, CancellationToken cancellationToken)
    {
        if (intervalSeconds == 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
                var snapshot = stats.Snapshot();
                Console.WriteLine(
                    $"Stats | in: {snapshot.InPackets} packets ({snapshot.InBytes} bytes) | " +
                    $"out: {snapshot.OutPackets} sends | failed: {snapshot.Failures}");
            }
        }, cancellationToken);
    }

    private static void PrintStartup(
        string configPath,
        IPAddress listenAddress,
        int listenPort,
        IReadOnlyCollection<TargetEndpoint> targets,
        int statsIntervalSeconds)
    {
        Console.WriteLine($"Config: {configPath}");
        Console.WriteLine($"Listening on {listenAddress}:{listenPort}");
        Console.WriteLine("Forwarding to:");

        foreach (var target in targets)
        {
            Console.WriteLine($"- {target.Label} -> {target.EndPoint.Address}:{target.EndPoint.Port}");
        }

        if (statsIntervalSeconds > 0)
        {
            Console.WriteLine($"Stats interval: {statsIntervalSeconds}s");
        }
        else
        {
            Console.WriteLine("Stats interval: disabled");
        }

        Console.WriteLine("Press Ctrl+C to stop.");
    }

    private static string GetTargetLabel(ForwardTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.Name))
        {
            return target.Name.Trim();
        }

        return $"{target.Host}:{target.Port}";
    }
}

internal sealed class ForwardStats
{
    private long _inPackets;
    private long _inBytes;
    private long _outPackets;
    private long _failures;

    public void RecordInbound(int payloadSize)
    {
        Interlocked.Increment(ref _inPackets);
        Interlocked.Add(ref _inBytes, payloadSize);
    }

    public void RecordForward()
    {
        Interlocked.Increment(ref _outPackets);
    }

    public void RecordFailure()
    {
        Interlocked.Increment(ref _failures);
    }

    public (long InPackets, long InBytes, long OutPackets, long Failures) Snapshot()
    {
        return (
            Interlocked.Read(ref _inPackets),
            Interlocked.Read(ref _inBytes),
            Interlocked.Read(ref _outPackets),
            Interlocked.Read(ref _failures)
        );
    }
}

internal sealed class SenderPool : IDisposable
{
    private readonly Dictionary<AddressFamily, UdpClient> _senders = [];
    private readonly object _lock = new();

    public async Task SendAsync(byte[] payload, IPEndPoint destination)
    {
        UdpClient sender;
        lock (_lock)
        {
            if (!_senders.TryGetValue(destination.AddressFamily, out sender!))
            {
                sender = destination.AddressFamily switch
                {
                    AddressFamily.InterNetwork => new UdpClient(AddressFamily.InterNetwork),
                    AddressFamily.InterNetworkV6 => new UdpClient(AddressFamily.InterNetworkV6),
                    _ => throw new InvalidOperationException(
                        $"Target address family '{destination.AddressFamily}' is not supported.")
                };

                _senders[destination.AddressFamily] = sender;
            }
        }

        await sender.SendAsync(payload, destination);
    }

    public void Dispose()
    {
        foreach (var sender in _senders.Values)
        {
            sender.Dispose();
        }

        _senders.Clear();
    }
}

internal sealed class ErrorLogGate
{
    private readonly TimeSpan _minimumInterval;
    private readonly Dictionary<string, DateTimeOffset> _lastLogByKey = [];
    private readonly object _lock = new();

    public ErrorLogGate(TimeSpan minimumInterval)
    {
        _minimumInterval = minimumInterval;
    }

    public bool ShouldLog(string key)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            if (!_lastLogByKey.TryGetValue(key, out var lastLog) || now - lastLog > _minimumInterval)
            {
                _lastLogByKey[key] = now;
                return true;
            }
        }

        return false;
    }
}

internal sealed class ForwarderConfig
{
    [JsonPropertyName("listenHost")]
    public string ListenHost { get; set; } = "0.0.0.0";

    [JsonPropertyName("listenPort")]
    public int ListenPort { get; set; }

    [JsonPropertyName("statsIntervalSeconds")]
    public int StatsIntervalSeconds { get; set; } = 5;

    [JsonPropertyName("targets")]
    public List<ForwardTarget> Targets { get; set; } = [];
}

internal sealed class ForwardTarget
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = "127.0.0.1";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

internal sealed record TargetEndpoint(string Label, IPEndPoint EndPoint);
