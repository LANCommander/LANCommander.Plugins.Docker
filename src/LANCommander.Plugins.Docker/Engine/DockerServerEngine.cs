using System.Collections.Concurrent;
using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using LANCommander.Plugins.Docker.Components;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Docker.Engine;

/// <summary>Runs game servers as Docker containers on the plugin's Docker hosts.</summary>
public sealed class DockerServerEngine(
    DockerHosts hosts,
    IConfiguration configuration,
    ILogger<DockerServerEngine> logger) : IServerEngineExtension, IAsyncDisposable
{
    /// <summary>
    /// The engine's id. Servers that used the server's built-in Docker engine were given it when the
    /// server's database was upgraded, so it must not change.
    /// </summary>
    public const string EngineId = "lancommander.docker";

    private readonly ConcurrentDictionary<Guid, ContainerWatcher> _watchers = new();

    // Previous CPU counters per server; a one-shot stats call has no earlier sample to compare with,
    // so usage is worked out across consecutive calls
    private readonly ConcurrentDictionary<Guid, (ulong Container, ulong System)> _cpuSamples = new();

    private readonly ContainerConsoles _consoles = new(hosts);

    private readonly ContainerFiles _files = new(hosts);

    private IServerEngineHost? _host;

    public string Id => EngineId;

    public string Name => "Docker";

    public ServerEngineFeatures Features => ServerEngineFeatures.Output | ServerEngineFeatures.Input | ServerEngineFeatures.ProcessInfo | ServerEngineFeatures.Consoles | ServerEngineFeatures.Files;

    public Type? EditorComponent => typeof(DockerServerEditor);

    public Type? SettingsComponent => typeof(DockerEngineSettings);

    public IReadOnlyList<ServerEngineSection> Sections { get; } = [new("Container", "Container", typeof(ContainerSection))];

    public Task InitializeAsync(IServerEngineHost host, CancellationToken cancellationToken)
    {
        _host = host;

        try
        {
            hosts.ImportLegacyHosts(configuration);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not take over the Docker hosts from the server's settings");
        }

        return Task.CompletedTask;
    }

    public async Task ServersChangedAsync(IReadOnlyList<ServerEngineServer> servers, CancellationToken cancellationToken)
    {
        if (_host == null)
            return;

        foreach (var server in servers)
        {
            var settings = server.GetSettings<DockerServerSettings>();

            if (_watchers.TryGetValue(server.Id, out var watcher))
                watcher.Update(settings);
            else
                _watchers[server.Id] = new ContainerWatcher(server.Id, settings, hosts, _host, logger);
        }

        foreach (var id in _watchers.Keys.Except(servers.Select(s => s.Id)).ToList())
        {
            if (_watchers.TryRemove(id, out var removed))
                await removed.DisposeAsync();
        }
    }

    public async Task StartAsync(ServerEngineServer server, CancellationToken cancellationToken)
    {
        var (client, container) = Resolve(server);

        try
        {
            await client.Containers.StartContainerAsync(container, new ContainerStartParameters(), cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            throw new DockerHostException($"The container \"{container}\" doesn't exist on its Docker host. Choose another on the server's General page.");
        }

        Wake(server.Id);
    }

    public async Task StopAsync(ServerEngineServer server, CancellationToken cancellationToken)
    {
        var (client, container) = Resolve(server);

        try
        {
            await client.Containers.StopContainerAsync(container, new ContainerStopParameters(), cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            // Nothing left to stop
        }

        Wake(server.Id);
    }

    public async Task<ServerEngineStatus> GetStatusAsync(ServerEngineServer server, CancellationToken cancellationToken)
    {
        var settings = server.GetSettings<DockerServerSettings>();

        // A server not set up yet isn't running, rather than failing
        if (settings.HostId == null || string.IsNullOrWhiteSpace(settings.ContainerId))
            return ServerEngineStatus.Stopped;

        var (client, container) = Resolve(server);

        try
        {
            var inspected = await client.Containers.InspectContainerAsync(container, cancellationToken);

            return ContainerStatus.Of(inspected.State);
        }
        catch (DockerContainerNotFoundException)
        {
            return ServerEngineStatus.Error;
        }
    }

    public async Task<ServerEngineProcessInfo?> GetProcessInfoAsync(ServerEngineServer server, CancellationToken cancellationToken)
    {
        var (client, container) = Resolve(server);

        var inspected = await client.Containers.InspectContainerAsync(container, cancellationToken);

        if (inspected.State is not { Running: true })
        {
            _cpuSamples.TryRemove(server.Id, out _);

            return null;
        }

        DateTime? startTime = DateTime.TryParse(inspected.State.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startedAt)
            ? startedAt
            : null;

        var stats = new LastValue<ContainerStatsResponse>();

        await client.Containers.GetContainerStatsAsync(inspected.ID, new ContainerStatsParameters { Stream = false, OneShot = true }, stats, cancellationToken);

        double? cpuPercent = null;
        long? memoryBytes = null;

        if (stats.Value?.CPUStats is { } cpu)
        {
            var containerUsage = cpu.CPUUsage?.TotalUsage ?? 0;
            var systemUsage = cpu.SystemUsage;

            // system_cpu_usage already spans every CPU, so this is a share of the whole machine
            if (_cpuSamples.TryGetValue(server.Id, out var previous) && systemUsage > previous.System && containerUsage >= previous.Container)
                cpuPercent = Math.Clamp((double)(containerUsage - previous.Container) / (systemUsage - previous.System) * 100d, 0d, 100d);

            _cpuSamples[server.Id] = (containerUsage, systemUsage);
        }

        if (stats.Value?.MemoryStats is { } memory)
        {
            var usage = memory.Usage;

            // As `docker stats` shows it: without page cache that can be reclaimed
            if (memory.Stats != null && memory.Stats.TryGetValue("inactive_file", out var inactiveFile) && inactiveFile < usage)
                usage -= inactiveFile;

            memoryBytes = (long)usage;
        }

        return new ServerEngineProcessInfo(inspected.State.Pid > 0 ? (int)inspected.State.Pid : null, startTime, cpuPercent, memoryBytes);
    }

    public bool CanSendInput(ServerEngineServer server) =>
        _watchers.TryGetValue(server.Id, out var watcher) && watcher.CanSendInput;

    public Task<bool> SendInputAsync(ServerEngineServer server, string line, CancellationToken cancellationToken) =>
        _watchers.TryGetValue(server.Id, out var watcher)
            ? watcher.SendInputAsync(line, cancellationToken)
            : Task.FromResult(false);

    public IReadOnlyList<ServerEngineConsole> GetConsoles(ServerEngineServer server) =>
        ContainerConsoles.For(server.GetSettings<DockerServerSettings>());

    public IAsyncEnumerable<ServerEngineLogLine> ReadLogAsync(ServerEngineServer server, string consoleId, ServerEngineLogRequest request, CancellationToken cancellationToken) =>
        _consoles.ReadLogAsync(server.GetSettings<DockerServerSettings>(), consoleId, request, cancellationToken);

    public Task<IServerEngineTerminal> OpenTerminalAsync(ServerEngineServer server, string consoleId, ServerEngineTerminalSize size, CancellationToken cancellationToken) =>
        _consoles.OpenAsync(server.GetSettings<DockerServerSettings>(), consoleId, size, cancellationToken);

    public Task<IReadOnlyList<ServerEngineFile>> ListFilesAsync(ServerEngineServer server, string path, CancellationToken cancellationToken) =>
        _files.ListAsync(server.GetSettings<DockerServerSettings>(), path, cancellationToken);

    public Task<Stream> OpenFileAsync(ServerEngineServer server, string path, CancellationToken cancellationToken) =>
        _files.OpenAsync(server.GetSettings<DockerServerSettings>(), path, cancellationToken);

    public Task WriteFileAsync(ServerEngineServer server, string path, Stream content, CancellationToken cancellationToken) =>
        _files.WriteAsync(server.GetSettings<DockerServerSettings>(), path, content, cancellationToken);

    public Task DeleteFileAsync(ServerEngineServer server, string path, CancellationToken cancellationToken) =>
        _files.DeleteAsync(server.GetSettings<DockerServerSettings>(), path, cancellationToken);

    public Task CreateDirectoryAsync(ServerEngineServer server, string path, CancellationToken cancellationToken) =>
        _files.CreateDirectoryAsync(server.GetSettings<DockerServerSettings>(), path, cancellationToken);

    private (DockerClient Client, string Container) Resolve(ServerEngineServer server)
    {
        var settings = server.GetSettings<DockerServerSettings>();

        if (string.IsNullOrWhiteSpace(settings.ContainerId))
            throw new DockerHostException("No container has been chosen for this server. Choose one on its General page.");

        return (hosts.GetClient(settings.HostId), settings.ContainerId);
    }

    /// <summary>Completes once the server's container has first been looked at, for tests.</summary>
    internal Task WatchingAsync(Guid serverId) =>
        _watchers.TryGetValue(serverId, out var watcher) ? watcher.FirstLook : Task.CompletedTask;

    private void Wake(Guid serverId)
    {
        if (_watchers.TryGetValue(serverId, out var watcher))
            watcher.Wake();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var watcher in _watchers.Values)
            await watcher.DisposeAsync();

        _watchers.Clear();
    }

    private sealed class LastValue<T> : IProgress<T>
    {
        public T? Value { get; private set; }

        public void Report(T value) => Value = value;
    }
}
