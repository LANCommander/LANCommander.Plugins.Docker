using System.Globalization;
using System.Runtime.CompilerServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.Server.Plugins;

namespace LANCommander.Plugins.Docker.Engine;

/// <summary>
/// The consoles the Docker engine offers for a server's container: its log, as <c>docker logs</c>
/// shows it; its main process's console, as <c>docker attach</c> joins it; and a shell beside it, as
/// <c>docker exec -it</c> opens one.
/// </summary>
internal sealed class ContainerConsoles(DockerHosts hosts)
{
    public const string LogId = "log";
    public const string AttachId = "attach";
    public const string ShellId = "shell";

    // Tries bash first, for its line editing, and falls back on the sh every image has
    private static readonly string[] ShellCommand = ["/bin/sh", "-c", "if command -v bash >/dev/null 2>&1; then exec bash; else exec sh; fi"];

    public static IReadOnlyList<ServerEngineConsole> For(DockerServerSettings settings) =>
        settings.HostId == null || string.IsNullOrWhiteSpace(settings.ContainerId)
            ? []
            :
            [
                new(LogId, "Container log", ServerEngineConsoleKind.Log),
                new(AttachId, "Attach", ServerEngineConsoleKind.Terminal),
                new(ShellId, "Shell", ServerEngineConsoleKind.Terminal),
            ];

    public async IAsyncEnumerable<ServerEngineLogLine> ReadLogAsync(DockerServerSettings settings, ServerEngineLogRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = hosts.GetClient(settings.HostId);

        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Timestamps = true,
            Follow = request.Follow,
            Tail = request.Tail?.ToString(CultureInfo.InvariantCulture) ?? "all",
        };

        using var stream = await OrNotFound(() => client.Containers.GetContainerLogsAsync(settings.ContainerId, parameters, cancellationToken), settings);

        var buffer = new byte[16 * 1024];
        var pending = new List<ServerEngineLogLine>();
        var readers = new Dictionary<MultiplexedStream.TargetStream, LineReader>();

        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);

            if (read.EOF)
                break;

            if (!readers.TryGetValue(read.Target, out var reader))
            {
                var isError = read.Target == MultiplexedStream.TargetStream.StandardError;

                readers[read.Target] = reader = new LineReader(line => pending.Add(ToLogLine(line, isError)));
            }

            reader.Append(buffer, read.Count);

            foreach (var line in pending)
                yield return line;

            pending.Clear();
        }

        foreach (var reader in readers.Values)
            reader.Flush();

        foreach (var line in pending)
            yield return line;
    }

    /// <summary>A line as <c>docker logs --timestamps</c> writes it: an RFC 3339 time, a space, the line.</summary>
    internal static ServerEngineLogLine ToLogLine(string line, bool isError)
    {
        var space = line.IndexOf(' ');

        if (space > 0 && TryParseTimestamp(line.AsSpan(0, space), out var timestamp))
            return new ServerEngineLogLine(line[(space + 1)..], timestamp, isError);

        return new ServerEngineLogLine(line, null, isError);
    }

    // Docker writes nanoseconds, which DateTime can't parse; it keeps 7 digits
    private static bool TryParseTimestamp(ReadOnlySpan<char> text, out DateTime timestamp)
    {
        var value = text.ToString();
        var dot = value.IndexOf('.');

        if (dot > 0)
        {
            var end = dot + 1;

            while (end < value.Length && char.IsAsciiDigit(value[end]))
                end++;

            if (end - dot - 1 > 7)
                value = value[..(dot + 8)] + value[end..];
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out timestamp);
    }

    public async Task<IServerEngineTerminal> OpenAsync(DockerServerSettings settings, string consoleId, ServerEngineTerminalSize size, CancellationToken cancellationToken)
    {
        var client = hosts.GetClient(settings.HostId);
        var container = await OrNotFound(() => client.Containers.InspectContainerAsync(settings.ContainerId, cancellationToken), settings);

        if (container.State is not { Running: true })
            throw new DockerHostException("The container isn't running. Start the server first.");

        return consoleId == ShellId
            ? await OpenShellAsync(client, container, size, cancellationToken)
            : await AttachAsync(client, container, size, cancellationToken);
    }

    private static async Task<IServerEngineTerminal> OpenShellAsync(DockerClient client, ContainerInspectResponse container, ServerEngineTerminalSize size, CancellationToken cancellationToken)
    {
        // Docker sizes a terminal as height, then width
        ulong[] consoleSize = [(ulong)Math.Max(size.Rows, 1), (ulong)Math.Max(size.Columns, 1)];

        var exec = await client.Exec.CreateContainerExecAsync(container.ID, new ContainerExecCreateParameters
        {
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            Tty = true,
            ConsoleSize = consoleSize,
            Env = ["TERM=xterm-256color"],
            Cmd = ShellCommand,
        }, cancellationToken);

        var stream = await client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters
        {
            Tty = true,
            ConsoleSize = consoleSize,
        }, cancellationToken);

        // The client has no call to resize an exec's terminal, so it keeps the size it opened at.
        // Docker leaves an exec running when its connection closes; an end of input at the prompt
        // ends the shell, as Ctrl+D would.
        return new ContainerTerminal(stream, closing: "\x04"u8.ToArray());
    }

    private static async Task<IServerEngineTerminal> AttachAsync(DockerClient client, ContainerInspectResponse container, ServerEngineTerminalSize size, CancellationToken cancellationToken)
    {
        var tty = container.Config?.Tty == true;
        var openStdin = container.Config?.OpenStdin == true;

        var stream = await client.Containers.AttachContainerAsync(container.ID, new ContainerAttachParameters
        {
            Stream = true,
            Stdin = openStdin,
            Stdout = true,
            Stderr = true,
        }, cancellationToken);

        if (!tty)
            return new LineTerminal(new ContainerTerminal(stream), openStdin);

        var terminal = new ContainerTerminal(stream, resize: (next, token) =>
            client.Containers.ResizeContainerTtyAsync(container.ID, new ContainerResizeParameters { Height = next.Rows, Width = next.Columns }, token));

        try
        {
            await terminal.ResizeAsync(size, cancellationToken);
        }
        catch (DockerApiException)
        {
            // It keeps the size it had
        }

        // Nothing is shown until the server next writes; a redraw would need a key it may not take
        return terminal;
    }

    private static async Task<T> OrNotFound<T>(Func<Task<T>> call, DockerServerSettings settings)
    {
        try
        {
            return await call();
        }
        catch (DockerContainerNotFoundException)
        {
            throw new DockerHostException($"The container \"{settings.ContainerId}\" doesn't exist on its Docker host.");
        }
    }
}

/// <summary>A session over a hijacked Docker connection: an exec's, or an attach's.</summary>
internal sealed class ContainerTerminal(
    MultiplexedStream stream,
    Func<ServerEngineTerminalSize, CancellationToken, Task>? resize = null,
    byte[]? closing = null) : IServerEngineTerminal
{
    private byte[] _buffer = new byte[16 * 1024];
    private int _disposed;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_buffer.Length < buffer.Length)
            _buffer = new byte[buffer.Length];

        while (true)
        {
            var read = await stream.ReadOutputAsync(_buffer, 0, buffer.Length, cancellationToken);

            if (read.EOF)
                return 0;

            if (read.Count == 0)
                continue;

            _buffer.AsMemory(0, read.Count).CopyTo(buffer);

            return read.Count;
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        var bytes = input.ToArray();

        await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
    }

    public Task ResizeAsync(ServerEngineTerminalSize size, CancellationToken cancellationToken) =>
        resize == null || size.Columns <= 0 || size.Rows <= 0 ? Task.CompletedTask : resize(size, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        if (closing != null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                await stream.WriteAsync(closing, 0, closing.Length, timeout.Token);
            }
            catch (Exception)
            {
                // The session is already gone
            }
        }

        stream.Dispose();
    }
}

