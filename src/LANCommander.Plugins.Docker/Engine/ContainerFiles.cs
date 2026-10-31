using System.Globalization;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.Server.Plugins;

namespace LANCommander.Plugins.Docker.Engine;

/// <summary>
/// A server's files, in its container under the folder <see cref="DockerServerSettings.FilesPath"/>
/// names, or the container's working directory. Each operation is a short <c>docker exec</c> of
/// <c>sh</c> in the running container, as the container's own user, so files are written with the
/// owner and permissions the server expects and links are followed as the server would follow them.
/// </summary>
/// <remarks>
/// Paths arrive checked by the server: relative, "/" between folders, no "..". They aren't confined
/// any further; the container's Shell console reaches everything anyway.
/// </remarks>
internal sealed class ContainerFiles(DockerHosts hosts)
{
    // Exit codes the scripts below use to say what went wrong, beside the shell's own 126 and 127
    private const int NotFound = 2;
    private const int WrongKind = 3;

    // Every entry of the folder, hidden ones too, with links followed: "type/size/mtime/name". Broken
    // links can't be followed, so are left out.
    private const string ListScript =
        """
        cd -- "$1" 2>/dev/null || { if [ -e "$1" ]; then echo "It isn't a folder." >&2; exit 3; fi; echo "There is no such folder." >&2; exit 2; }
        set --
        for f in * .[!.]* ..?*; do
          if [ -e "$f" ]; then set -- "$@" "$f"; fi
        done
        [ "$#" -eq 0 ] && exit 0
        stat -L -c '%F/%s/%Y/%n' -- "$@" 2>/dev/null
        exit 0
        """;

    private const string ReadScript =
        """
        if [ -d "$1" ]; then echo "It is a folder." >&2; exit 3; fi
        if [ ! -e "$1" ]; then echo "There is no such file." >&2; exit 2; fi
        exec cat -- "$1"
        """;

    // Truncating in place keeps an existing file's owner and mode; a new one gets the container user's
    private const string WriteScript =
        """
        if [ -d "$1" ]; then echo "It is a folder." >&2; exit 3; fi
        mkdir -p -- "$(dirname -- "$1")" || exit 1
        exec cat > "$1"
        """;

    private const string DeleteScript =
        """
        if [ ! -e "$1" ] && [ ! -L "$1" ]; then echo "There is no such file or folder." >&2; exit 2; fi
        exec rm -rf -- "$1"
        """;

    private const string CreateDirectoryScript = """exec mkdir -p -- "$1" """;

    public async Task<IReadOnlyList<ServerEngineFile>> ListAsync(DockerServerSettings settings, string path, CancellationToken cancellationToken)
    {
        var (client, container, root) = await ResolveAsync(settings, cancellationToken);
        var target = InContainer(root, path);

        var (output, error, exitCode) = await RunAsync(client, container, ListScript, target, null, cancellationToken);

        ThrowIfFailed(exitCode, error, target, directory: true);

        return ParseListing(output);
    }

    public async Task<Stream> OpenAsync(DockerServerSettings settings, string path, CancellationToken cancellationToken)
    {
        var (client, container, root) = await ResolveAsync(settings, cancellationToken);
        var target = InContainer(root, path);

        var (execId, stream) = await StartAsync(client, container, ReadScript, target, stdin: false, cancellationToken);
        var reader = new ExecOutputStream(client, execId, stream, (exitCode, error) => ThrowIfFailed(exitCode, error, target, directory: false));

        try
        {
            // So a missing file is reported now rather than on the first read
            await reader.PrimeAsync(cancellationToken);

            return reader;
        }
        catch
        {
            await reader.DisposeAsync();

            throw;
        }
    }

    public async Task WriteAsync(DockerServerSettings settings, string path, Stream content, CancellationToken cancellationToken)
    {
        var (client, container, root) = await ResolveAsync(settings, cancellationToken);
        var target = InContainer(root, path);

        var (_, error, exitCode) = await RunAsync(client, container, WriteScript, target, content, cancellationToken);

        ThrowIfFailed(exitCode, error, target, directory: false);
    }

    public async Task DeleteAsync(DockerServerSettings settings, string path, CancellationToken cancellationToken)
    {
        var (client, container, root) = await ResolveAsync(settings, cancellationToken);
        var target = InContainer(root, path);

        if (target == root)
            throw new InvalidOperationException("The server's files folder itself can't be deleted.");

        var (_, error, exitCode) = await RunAsync(client, container, DeleteScript, target, null, cancellationToken);

        ThrowIfFailed(exitCode, error, target, directory: false);
    }

    public async Task CreateDirectoryAsync(DockerServerSettings settings, string path, CancellationToken cancellationToken)
    {
        var (client, container, root) = await ResolveAsync(settings, cancellationToken);
        var target = InContainer(root, path);

        var (_, error, exitCode) = await RunAsync(client, container, CreateDirectoryScript, target, null, cancellationToken);

        ThrowIfFailed(exitCode, error, target, directory: true);
    }

    /// <summary>
    /// The folder in the container the server's files are under: the one the server's settings name,
    /// or the container's working directory, or its root.
    /// </summary>
    internal static string RootOf(DockerServerSettings settings, string? workingDirectory)
    {
        var root = !string.IsNullOrWhiteSpace(settings.FilesPath) ? settings.FilesPath.Trim() : workingDirectory;

        if (string.IsNullOrWhiteSpace(root))
            return "/";

        root = root.Replace('\\', '/');

        if (!root.StartsWith('/'))
            root = "/" + root;

        root = root.TrimEnd('/');

        return root.Length == 0 ? "/" : root;
    }

    /// <summary>A path among the server's files as the container names it.</summary>
    internal static string InContainer(string root, string path)
    {
        // The server never sends these; this is the last place they could do harm
        if (path.StartsWith('/') || path.Split('/').Any(segment => segment is ".."))
            throw new ArgumentException("The path can't leave the server's files.", nameof(path));

        return path.Length == 0 ? root
            : root == "/" ? "/" + path
            : $"{root}/{path}";
    }

    /// <summary>What <see cref="ListScript"/> writes, one entry a line.</summary>
    internal static IReadOnlyList<ServerEngineFile> ParseListing(string output)
    {
        var files = new List<ServerEngineFile>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // A name can't hold "/", so the name is everything after the third
            var parts = line.TrimEnd('\r').Split('/', 4);

            if (parts.Length < 4 || parts[3].Length == 0)
                continue;

            var isDirectory = parts[0] == "directory";

            long? size = !isDirectory && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var length) ? length : null;

            DateTime? modified = long.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                : null;

            files.Add(new ServerEngineFile(parts[3], isDirectory, size, modified));
        }

        return files;
    }

    /// <summary>Turns a script's exit code into the exception the server expects.</summary>
    internal static void ThrowIfFailed(long exitCode, string error, string target, bool directory)
    {
        var message = error.Trim();

        switch (exitCode)
        {
            case 0:
                return;

            case NotFound when directory:
                throw new DirectoryNotFoundException($"There is no folder {target} in the container.");

            case NotFound:
                throw new FileNotFoundException($"There is no file {target} in the container.", target);

            case WrongKind:
                throw new IOException($"{target}: {message}");

            // The shell, or a command the scripts use, is missing, as in images built from scratch
            case 126 or 127:
                throw new DockerHostException($"The container can't run the commands needed to manage its files (sh, stat, cat, mkdir, rm): {message}");

            default:
                throw new IOException(message.Length > 0 ? $"{target}: {message}" : $"{target} could not be changed (exit code {exitCode}).");
        }
    }

    private async Task<(DockerClient Client, string Container, string Root)> ResolveAsync(DockerServerSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ContainerId))
            throw new DockerHostException("No container has been chosen for this server. Choose one on its General page.");

        var client = hosts.GetClient(settings.HostId);

        ContainerInspectResponse container;

        try
        {
            container = await client.Containers.InspectContainerAsync(settings.ContainerId, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            throw new DockerHostException($"The container \"{settings.ContainerId}\" doesn't exist on its Docker host.");
        }

        // Docker can only run commands in a running container
        if (container.State is not { Running: true })
            throw new DockerHostException("The container isn't running. Start the server to manage its files.");

        return (client, container.ID, RootOf(settings, container.Config?.WorkingDir));
    }

    private static async Task<(string Output, string Error, long ExitCode)> RunAsync(DockerClient client, string container, string script, string target, Stream? input, CancellationToken cancellationToken)
    {
        var (execId, stream) = await StartAsync(client, container, script, target, stdin: input != null, cancellationToken);

        using (stream)
        {
            if (input != null)
            {
                await stream.CopyFromAsync(input, cancellationToken);

                stream.CloseWrite();
            }

            var (output, error) = await stream.ReadOutputToEndAsync(cancellationToken);

            return (output, error, await ExitCodeAsync(client, execId, cancellationToken));
        }
    }

    private static async Task<(string ExecId, MultiplexedStream Stream)> StartAsync(DockerClient client, string container, string script, string target, bool stdin, CancellationToken cancellationToken)
    {
        var exec = await client.Exec.CreateContainerExecAsync(container, new ContainerExecCreateParameters
        {
            AttachStdin = stdin,
            AttachStdout = true,
            AttachStderr = true,
            // The path is the script's $1, so it is never read as shell
            Cmd = ["sh", "-c", script, "sh", target],
        }, cancellationToken);

        var stream = await client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters(), cancellationToken);

        return (exec.ID, stream);
    }

    // The output can end a moment before Docker records how the command exited
    internal static async Task<long> ExitCodeAsync(DockerClient client, string execId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var exec = await client.Exec.InspectContainerExecAsync(execId, cancellationToken);

            if (!exec.Running || attempt >= 50)
                return exec.ExitCode;

            await Task.Delay(100, cancellationToken);
        }
    }
}

/// <summary>
/// What a command in a container writes to its standard output, read as it arrives. Its error output
/// is kept, and once the output ends, a command that failed is reported by
/// <c>onExit</c>.
/// </summary>
internal sealed class ExecOutputStream(DockerClient client, string execId, MultiplexedStream stream, Action<long, string> onExit) : Stream
{
    private readonly StringBuilder _error = new();
    private byte[] _frame = new byte[81920];
    private ReadOnlyMemory<byte> _pending;
    private bool _ended;

    /// <summary>Reads ahead to the first output, so a command that fails at once is reported now.</summary>
    public async Task PrimeAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[_frame.Length];
        var read = await ReadAsync(buffer, cancellationToken);

        _pending = buffer.AsMemory(0, read);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_pending.IsEmpty)
        {
            var count = Math.Min(buffer.Length, _pending.Length);

            _pending[..count].CopyTo(buffer);
            _pending = _pending[count..];

            return count;
        }

        if (_ended || buffer.Length == 0)
            return 0;

        if (_frame.Length < buffer.Length)
            _frame = new byte[buffer.Length];

        while (true)
        {
            var read = await stream.ReadOutputAsync(_frame, 0, buffer.Length, cancellationToken);

            if (read.EOF)
            {
                _ended = true;

                onExit(await ContainerFiles.ExitCodeAsync(client, execId, cancellationToken), _error.ToString());

                return 0;
            }

            if (read.Target == MultiplexedStream.TargetStream.StandardError)
            {
                _error.Append(Encoding.UTF8.GetString(_frame, 0, read.Count));
                continue;
            }

            if (read.Count == 0)
                continue;

            _frame.AsMemory(0, read.Count).CopyTo(buffer);

            return read.Count;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            stream.Dispose();

        base.Dispose(disposing);
    }
}
