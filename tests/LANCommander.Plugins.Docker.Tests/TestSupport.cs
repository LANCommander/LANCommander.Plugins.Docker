using System.Collections.Concurrent;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.Docker.Tests;

internal sealed class ScratchDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docker-plugin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows keeps the plugin's assemblies locked while they're loaded
        }
    }
}

/// <summary>Plugin settings kept in memory, as the host keeps them in a file.</summary>
internal sealed class MemorySettings<T> : IPluginSettings<T> where T : class, new()
{
    public T Current { get; } = new();

    public event Action? Changed;

    public void Update(Action<T> mutator)
    {
        mutator(Current);
        Changed?.Invoke();
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>What an engine reports, as the server would receive it.</summary>
internal sealed class RecordingEngineHost : IServerEngineHost
{
    public ConcurrentQueue<(Guid ServerId, string Line)> Output { get; } = new();
    public ConcurrentQueue<(Guid ServerId, ServerEngineStatus Status, string? Error)> Statuses { get; } = new();

    public void ReportStatus(Guid serverId, ServerEngineStatus status, string? error = null) => Statuses.Enqueue((serverId, status, error));

    public void WriteOutput(Guid serverId, string line) => Output.Enqueue((serverId, line));
}

internal static class TestHosts
{
    public static DockerHosts Create(params DockerHost[] hosts)
    {
        var settings = new MemorySettings<DockerSettings>();

        settings.Current.Hosts.AddRange(hosts);

        return new DockerHosts(settings, NullLogger<DockerHosts>.Instance);
    }

    /// <summary>Waits for a condition the code under test meets in the background.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}");

            await Task.Delay(100);
        }
    }
}
