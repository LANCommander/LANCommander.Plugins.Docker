using System.Globalization;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Docker.Engine;

/// <summary>
/// Follows one server's container for as long as the server uses the Docker engine: passes its output
/// on while it runs, reports when it stops or fails on its own, and writes commands to its input.
/// </summary>
internal sealed class ContainerWatcher : IAsyncDisposable
{
    // How often a container that isn't running is looked at, to notice it being started elsewhere
    internal static TimeSpan IdleInterval = TimeSpan.FromSeconds(3);

    // How long to wait after the host couldn't be reached before trying again
    internal static TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    // Lines of a running container's earlier output shown when the watcher first finds it
    private const int BacklogLines = 200;

    private readonly Guid _serverId;
    private readonly DockerHosts _hosts;
    private readonly IServerEngineHost _engineHost;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly Task _loop;

    private volatile DockerServerSettings _settings;
    private CancellationTokenSource _settingsChanged = new();

    // What the last look at the container found
    private volatile bool _running;
    private volatile bool _openStdin;
    private string? _containerId;

    private MultiplexedStream? _input;

    private readonly TaskCompletionSource _firstLook = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the watcher has first looked at the container, for tests.</summary>
    internal Task FirstLook => _firstLook.Task;

    public ContainerWatcher(Guid serverId, DockerServerSettings settings, DockerHosts hosts, IServerEngineHost engineHost, ILogger logger)
    {
        _serverId = serverId;
        _settings = settings;
        _hosts = hosts;
        _engineHost = engineHost;
        _logger = logger;
        _loop = Task.Run(RunAsync);
    }

    public bool CanSendInput => _running && _openStdin;

    /// <summary>The log files the container's image names in its labels, as last looked at.</summary>
    public IReadOnlyList<ContainerLogFile> ImageLogFiles { get; private set; } = [];

    /// <summary>Follows another container, or the same one on another host.</summary>
    public void Update(DockerServerSettings settings)
    {
        if (settings.HostId == _settings.HostId && settings.ContainerId == _settings.ContainerId)
            return;

        _settings = settings;

        var previous = Interlocked.Exchange(ref _settingsChanged, new CancellationTokenSource());

        previous.Cancel();
        previous.Dispose();

        Wake();
    }

    /// <summary>Looks at the container now rather than at the next interval, e.g. after starting it.</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    public async Task<bool> SendInputAsync(string line, CancellationToken cancellationToken)
    {
        if (!CanSendInput)
            return false;

        await _inputLock.WaitAsync(cancellationToken);

        try
        {
            if (_input == null)
            {
                var settings = _settings;

                _input = await _hosts.GetClient(settings.HostId).Containers.AttachContainerAsync(
                    _containerId ?? settings.ContainerId,
                    new ContainerAttachParameters { Stream = true, Stdin = true },
                    _stopping.Token);
            }

            var bytes = Encoding.UTF8.GetBytes(line + "\n");

            await _input.WriteAsync(bytes, 0, bytes.Length, cancellationToken);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not write to the input of server {ServerId}'s container", _serverId);

            CloseInput();

            return false;
        }
        finally
        {
            _inputLock.Release();
        }
    }

    private async Task RunAsync()
    {
        var firstLook = true;
        var wasRunning = false;

        // When the latest run that has been accounted for started: one whose end has been reported, or
        // that was already over when the watcher first looked
        string? accountedRun = null;

        while (!_stopping.IsCancellationRequested)
        {
            var settings = _settings;
            var changed = _settingsChanged.Token;

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, changed);

            try
            {
                if (settings.HostId == null || string.IsNullOrWhiteSpace(settings.ContainerId))
                {
                    _running = false;
                    ImageLogFiles = [];
                    await WaitAsync(Timeout.InfiniteTimeSpan, cancellation.Token);
                    continue;
                }

                var client = _hosts.GetClient(settings.HostId);
                var container = await client.Containers.InspectContainerAsync(settings.ContainerId, cancellation.Token);

                _containerId = container.ID;
                ImageLogFiles = ContainerLogFile.FromLabels(container.Config?.Labels);
                _openStdin = container.Config?.OpenStdin == true;
                _running = container.State?.Running == true;

                if (_running)
                {
                    if (!firstLook && !wasRunning)
                        _engineHost.ReportStatus(_serverId, ServerEngineStatus.Running);

                    wasRunning = true;

                    _firstLook.TrySetResult();

                    await FollowOutputAsync(client, container, firstLook, cancellation.Token);

                    firstLook = false;

                    // The output ends when the container stops; see how it went
                    var stopped = await client.Containers.InspectContainerAsync(container.ID, cancellation.Token);

                    _running = stopped.State?.Running == true;

                    if (!_running)
                    {
                        CloseInput();
                        ReportEnd(stopped.State);

                        accountedRun = stopped.State?.StartedAt;
                        wasRunning = false;
                    }
                }
                else
                {
                    var run = container.State?.StartedAt;

                    if (firstLook)
                    {
                        accountedRun = run;
                        _firstLook.TrySetResult();
                    }
                    else if (run != accountedRun)
                    {
                        // A run started and ended between looks, e.g. a server that fails as soon as
                        // it starts: pass on what it said, and how it ended
                        await FollowOutputAsync(client, container, includeBacklog: false, cancellation.Token);

                        ReportEnd(container.State);

                        accountedRun = run;
                    }

                    firstLook = false;
                    wasRunning = false;

                    await WaitAsync(IdleInterval, cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Stopping, or following another container
                CloseInput();
                firstLook = true;
                wasRunning = false;
            }
            catch (Exception ex)
            {
                _running = false;
                CloseInput();

                _logger.LogDebug(ex, "Could not follow server {ServerId}'s container", _serverId);

                try
                {
                    await WaitAsync(RetryInterval, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private void ReportEnd(State? state)
    {
        var failure = state == null ? null : ContainerStatus.Failure(state);

        _engineHost.ReportStatus(_serverId, failure == null ? ServerEngineStatus.Stopped : ServerEngineStatus.Error, failure);
    }

    private async Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken) =>
        await _wake.WaitAsync(interval, cancellationToken);

    private async Task FollowOutputAsync(DockerClient client, ContainerInspectResponse container, bool includeBacklog, CancellationToken cancellationToken)
    {
        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = true,
        };

        // Only this run's output; and of a container found already running, only its latest lines
        if (DateTime.TryParse(container.State?.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startedAt))
            parameters.Since = new DateTimeOffset(startedAt).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        if (includeBacklog)
            parameters.Tail = BacklogLines.ToString(CultureInfo.InvariantCulture);
        else if (parameters.Since == null)
            parameters.Tail = "0";

        using var stream = await client.Containers.GetContainerLogsAsync(container.ID, parameters, cancellationToken);

        var buffer = new byte[16 * 1024];
        var lines = new Dictionary<MultiplexedStream.TargetStream, LineReader>();

        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);

            if (read.EOF)
                break;

            if (!lines.TryGetValue(read.Target, out var reader))
                lines[read.Target] = reader = new LineReader(line => _engineHost.WriteOutput(_serverId, line));

            reader.Append(buffer, read.Count);
        }

        foreach (var reader in lines.Values)
            reader.Flush();
    }

    private void CloseInput()
    {
        var input = Interlocked.Exchange(ref _input, null);

        input?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        CloseInput();

        _stopping.Dispose();
        _settingsChanged.Dispose();
        _wake.Dispose();
        _inputLock.Dispose();
    }
}
