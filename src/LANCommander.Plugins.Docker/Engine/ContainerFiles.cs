using System.Globalization;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.Server.Plugins;

namespace LANCommander.Plugins.Docker.Engine;

/// <summary>A folder or volume mounted into a container.</summary>
/// <param name="Destination">Where it is in the container, e.g. <c>/config</c>.</param>
/// <param name="Source">The folder on the Docker host, as written when the container was made, or the volume's name.</param>
/// <param name="IsFolder">Whether it's a folder on the Docker host rather than a volume.</param>
/// <param name="LocalPath">Where LANCommander finds the folder itself; null if it can't.</param>
public sealed record MountedFolder(string Destination, string Source, bool IsFolder, string? LocalPath);

/// <summary>Where a server's files are, as its Files page shows them.</summary>
/// <param name="Root">The folder in the container the Files page starts at.</param>
public sealed record FilesView(string Root, IReadOnlyList<MountedFolder> Mounts, bool Running);

/// <summary>Where a path in a container is to be found.</summary>
internal abstract record FileLocation
{
    /// <summary>In a folder mounted from the Docker host, which LANCommander reaches at this path.</summary>
    public sealed record OnHost(string Path) : FileLocation;

    /// <summary>A folder in the container that holds mounts, but isn't in one itself: only those are in it.</summary>
    public sealed record Between(IReadOnlyList<string> Children) : FileLocation;

    /// <summary>Only in the container, so only reached through it while it runs.</summary>
    public sealed record InContainer : FileLocation
    {
        public static readonly InContainer Instance = new();
    }
}

/// <summary>
/// A server's files: the folders mounted into its container, from the deepest folder holding them all.
/// Those in a host folder LANCommander can reach (<see cref="HostFolders"/>) are managed there
/// directly, running or not. The rest are managed through the container, while it runs, with a short
/// <c>docker exec</c> of <c>sh</c> as the container's own user.
/// </summary>
/// <remarks>
/// Paths arrive checked by the server: relative, "/" between folders, no "..". On the host, links are
/// followed only as far as they stay in the mounted folder; in the container they aren't confined
/// any further, as the container's Shell console reaches everything anyway.
/// </remarks>
public sealed class ContainerFiles(DockerHosts hosts)
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

    // Truncating in place keeps an existing file's owner and mode; a new one gets the container user's.
    // It reads exactly the file's length ($2) rather than to the end of its input: Docker Desktop's
    // Windows pipe can lose the end of an exec's input if it arrives before the data has drained
    private const string WriteScript =
        """
        if [ -d "$1" ]; then echo "It is a folder." >&2; exit 3; fi
        mkdir -p -- "$(dirname -- "$1")" || exit 1
        exec head -c "$2" > "$1"
        """;

    private const string DeleteScript =
        """
        if [ ! -e "$1" ] && [ ! -L "$1" ]; then echo "There is no such file or folder." >&2; exit 2; fi
        exec rm -rf -- "$1"
        """;

    private const string CreateDirectoryScript = """exec mkdir -p -- "$1" """;

    public async Task<IReadOnlyList<ServerEngineFile>> ListAsync(DockerServerSettings settings, string path, CancellationToken cancellationToken)
    {
        var view = await ResolveAsync(settings, cancellationToken);
        var target = InContainer(view.Root, path);

        switch (Locate(target, view.Mounts))
        {
            case FileLocation.OnHost(var local):
                return ListLocal(local, target, ChildMountsOf(target, view.Mounts));

            case FileLocation.Between(var children):
                return children.Select(name => new ServerEngineFile(name, true, null, null)).ToList();
        }

        var client = view.RequireRunning();
        var (output, error, exitCode) = await RunAsync(client, view.Container, ListScript, [target], null, cancellationToken);

        ThrowIfFailed(exitCode, error, target, directory: true);

        return ParseListing(output);
    }

    public async Task<Stream> OpenAsync(DockerServerSettings settings, string path, CancellationToken cancellationToken)
    {
        var view = await ResolveAsync(settings, cancellationToken);
        var target = InContainer(view.Root, path);

        switch (Locate(target, view.Mounts))
        {
            case FileLocation.OnHost(var local):
                if (Directory.Exists(local))
                    throw new IOException($"{target}: It is a folder.");

                if (!File.Exists(local))
                    throw new FileNotFoundException($"There is no file {target} in the container.", target);

                return new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

            case FileLocation.Between:
                throw new IOException($"{target}: It is a folder.");
        }

        var client = view.RequireRunning();
        var (execId, stream) = await StartAsync(client, view.Container, ReadScript, [target], stdin: false, cancellationToken);
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
        var view = await ResolveAsync(settings, cancellationToken);
        var target = InContainer(view.Root, path);

        switch (Locate(target, view.Mounts))
        {
            case FileLocation.OnHost(var local):
                if (Directory.Exists(local))
                    throw new IOException($"{target}: It is a folder.");

                Directory.CreateDirectory(Path.GetDirectoryName(local)!);

                // Truncated in place rather than replaced, so an existing file keeps its owner and mode
                await using (var file = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    await content.CopyToAsync(file, cancellationToken);

                return;

            case FileLocation.Between:
                throw NotInAMount(target);
        }

        var client = view.RequireRunning();

        // Its length is passed with it, so a stream that can't tell it is kept on disk first
        await using var buffered = content.CanSeek ? null : await BufferAsync(content, cancellationToken);
        var input = buffered ?? content;
        var length = input.Length - input.Position;

        var (_, error, exitCode) = await RunAsync(client, view.Container, WriteScript, [target, length.ToString(CultureInfo.InvariantCulture)], input, cancellationToken);

        ThrowIfFailed(exitCode, error, target, directory: false);
    }

    /// <summary>A copy of the stream in a temporary file, deleted when it's closed, from its start.</summary>
    private static async Task<Stream> BufferAsync(Stream content, CancellationToken cancellationToken)
    {
        var file = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        try
        {
            await content.CopyToAsync(file, cancellationToken);

            file.Position = 0;

            return file;
        }
        catch
        {
            await file.DisposeAsync();

            throw;
        }
    }

    public async Task DeleteAsync(DockerServerSettings settings, string path, CancellationToken cancellationToken)
    {
        var view = await ResolveAsync(settings, cancellationToken);
        var target = InContainer(view.Root, path);

        if (target == view.Root)
            throw new InvalidOperationException("The server's files folder itself can't be deleted.");

        // A mounted folder is the host's or a volume, not something in the container to delete
        if (view.Mounts.Any(m => IsUnder(m.Destination, target)))
            throw new InvalidOperationException($"{target} is, or holds, a folder mounted into the container, so it can't be deleted.");

        switch (Locate(target, view.Mounts))
        {
            case FileLocation.OnHost(var local):
                // A link is removed, not what it links to
                var entry = new FileInfo(local);

                if (entry.LinkTarget == null && Directory.Exists(local))
                    Directory.Delete(local, recursive: true);
                else if (entry.Exists || entry.LinkTarget != null)
                    entry.Delete();
                else
                    throw new FileNotFoundException($"There is no file or folder {target} in the container.", target);

                return;

            case FileLocation.Between:
                throw NotInAMount(target);
        }

        var client = view.RequireRunning();
        var (_, error, exitCode) = await RunAsync(client, view.Container, DeleteScript, [target], null, cancellationToken);

        ThrowIfFailed(exitCode, error, target, directory: false);
    }

    public async Task CreateDirectoryAsync(DockerServerSettings settings, string path, CancellationToken cancellationToken)
    {
        var view = await ResolveAsync(settings, cancellationToken);
        var target = InContainer(view.Root, path);

        switch (Locate(target, view.Mounts))
        {
            case FileLocation.OnHost(var local):
                Directory.CreateDirectory(local);
                return;

            case FileLocation.Between:
                throw NotInAMount(target);
        }

        var client = view.RequireRunning();
        var (_, error, exitCode) = await RunAsync(client, view.Container, CreateDirectoryScript, [target], null, cancellationToken);

        ThrowIfFailed(exitCode, error, target, directory: true);
    }

    /// <summary>Where the server's files are, for showing where they're managed from.</summary>
    public async Task<FilesView> DescribeAsync(DockerServerSettings settings, CancellationToken cancellationToken)
    {
        var view = await ResolveAsync(settings, cancellationToken);

        return new FilesView(view.Root, view.Mounts, view.Running);
    }

    private static IOException NotInAMount(string target) =>
        new($"{target} isn't in a folder mounted into the container, so nothing can be put there.");

    /// <summary>
    /// The folder in the container the server's files are under: the deepest folder that holds every
    /// folder mounted into it, so the server's own files are shown rather than the image's. Without
    /// mounts, the container's working directory, or its root.
    /// </summary>
    internal static string RootOf(IReadOnlyList<MountedFolder> mounts, string? workingDirectory)
    {
        if (mounts.Count == 0)
            return Normalize(workingDirectory);

        var common = mounts[0].Destination.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

        foreach (var mount in mounts.Skip(1))
        {
            var segments = mount.Destination.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var same = 0;

            while (same < common.Count && same < segments.Length && common[same] == segments[same])
                same++;

            common.RemoveRange(same, common.Count - same);
        }

        return "/" + string.Join('/', common);
    }

    /// <summary>A folder in the container as a path from its root, without a trailing "/".</summary>
    internal static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var normalized = path.Trim().Replace('\\', '/');

        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;

        normalized = normalized.TrimEnd('/');

        return normalized.Length == 0 ? "/" : normalized;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    internal static bool IsUnder(string path, string folder) =>
        folder == "/" || path == folder || path.StartsWith(folder + "/", StringComparison.Ordinal);

    /// <summary>
    /// Where a path in the container is to be found: in a mounted folder LANCommander can reach, in
    /// the folders leading to mounts, or only in the container.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">A link on the host leads out of its mounted folder.</exception>
    internal static FileLocation Locate(string target, IReadOnlyList<MountedFolder> mounts)
    {
        var mount = mounts
            .Where(m => IsUnder(target, m.Destination))
            .MaxBy(m => m.Destination.Length);

        if (mount != null)
        {
            if (mount.LocalPath == null)
                return FileLocation.InContainer.Instance;

            string[] relative = target.Length > mount.Destination.Length
                ? target[(mount.Destination == "/" ? 1 : mount.Destination.Length + 1)..].Split('/')
                : [];

            return new FileLocation.OnHost(HostFolders.Confine(mount.LocalPath, relative));
        }

        var children = ChildMountsOf(target, mounts);

        return children.Count > 0 ? new FileLocation.Between(children) : FileLocation.InContainer.Instance;
    }

    /// <summary>The names in a folder that lead to mounts below it.</summary>
    internal static IReadOnlyList<string> ChildMountsOf(string folder, IReadOnlyList<MountedFolder> mounts) =>
        mounts
            .Where(m => m.Destination != folder && IsUnder(m.Destination, folder))
            .Select(m => m.Destination[(folder == "/" ? 1 : folder.Length + 1)..].Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>A folder on the host, with the folders of any mounts below it, which may not be there yet.</summary>
    internal static IReadOnlyList<ServerEngineFile> ListLocal(string local, string target, IReadOnlyList<string> mountedBelow)
    {
        var folder = new DirectoryInfo(local);

        if (!folder.Exists)
        {
            if (File.Exists(local))
                throw new IOException($"{target}: It isn't a folder.");

            throw new DirectoryNotFoundException($"There is no folder {target} in the container.");
        }

        var files = new List<ServerEngineFile>();

        foreach (var entry in folder.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            // As the container would show it, with links followed; a broken one is left out, as there
            var isDirectory = Directory.Exists(entry.FullName);

            if (!isDirectory && !File.Exists(entry.FullName))
                continue;

            long? size = isDirectory ? null : LengthOf(entry.FullName);

            files.Add(new ServerEngineFile(entry.Name, isDirectory, size, entry.LastWriteTimeUtc));
        }

        foreach (var name in mountedBelow.Where(name => files.All(f => f.Name != name)))
            files.Add(new ServerEngineFile(name, true, null, null));

        return files;
    }

    // A link's own length isn't its file's
    private static long? LengthOf(string path)
    {
        try
        {
            var file = new FileInfo(path);

            return file.LinkTarget != null && file.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target ? target.Length : file.Length;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>A path among the server's files as the container names it.</summary>
    internal static string InContainer(string root, string path)
    {
        // The server never sends these; this is the last place they could do harm
        if (path.StartsWith('/') || path.Split('/').Any(segment => segment is ".."))
            throw new ArgumentException("The path can't leave the server's files.", nameof(path));

        path = path.TrimEnd('/');

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

    private async Task<ResolvedView> ResolveAsync(DockerServerSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ContainerId))
            throw new DockerHostException("No container has been chosen for this server. Choose one on its General page.");

        var client = hosts.GetClient(settings.HostId);
        var host = hosts.Find(settings.HostId)!;

        ContainerInspectResponse container;

        try
        {
            container = await client.Containers.InspectContainerAsync(settings.ContainerId, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            throw new DockerHostException($"The container \"{settings.ContainerId}\" doesn't exist on its Docker host.");
        }

        var mounts = MountsOf(container, host);

        return new ResolvedView(client, container.ID, container.State is { Running: true }, RootOf(mounts, container.Config?.WorkingDir), mounts);
    }

    /// <summary>The folders and volumes mounted into the container, with where LANCommander finds each of the host's folders.</summary>
    internal static IReadOnlyList<MountedFolder> MountsOf(ContainerInspectResponse container, DockerHost host)
    {
        var written = WrittenSources(container.HostConfig?.Binds);

        return (container.Mounts ?? [])
            .Where(m => m.Type is "bind" or "volume" && !string.IsNullOrWhiteSpace(m.Destination))
            .Select(m =>
            {
                var destination = Normalize(m.Destination);
                var isFolder = m.Type == "bind";
                var source = (isFolder && written.TryGetValue(destination, out var asWritten) ? asWritten : isFolder ? m.Source : m.Name) ?? "";

                string? local = null;

                if (isFolder && source.Length > 0)
                {
                    try
                    {
                        local = HostFolders.LocalPathOf(host, source);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        // A link leads out of the data folder: left to the container, which sees it as it is
                    }
                }

                return new MountedFolder(destination, source, isFolder, local);
            })
            .DistinctBy(m => m.Destination)
            .OrderBy(m => m.Destination, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The sources of the container's binds as they were written, by where they're mounted. Docker
    /// Desktop reports a Windows folder's mount at a path in its own machine instead.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> WrittenSources(IEnumerable<string>? binds)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var bind in binds ?? [])
        {
            // The destination is a Linux path, after the last ":/"; a Windows source has a ":" of its own
            var separator = bind.LastIndexOf(":/", StringComparison.Ordinal);

            if (separator <= 0)
                continue;

            var rest = bind[(separator + 1)..];
            var options = rest.IndexOf(':');

            sources.TryAdd(Normalize(options < 0 ? rest : rest[..options]), bind[..separator]);
        }

        return sources;
    }

    /// <summary>A container, as far as its files are concerned.</summary>
    private sealed record ResolvedView(DockerClient Client, string Container, bool Running, string Root, IReadOnlyList<MountedFolder> Mounts)
    {
        // Docker can only run commands in a running container
        public DockerClient RequireRunning() => Running
            ? Client
            : throw new DockerHostException(Mounts.Any(m => m.IsFolder)
                ? "The container isn't running, and LANCommander can't reach these files on the Docker host. Start the server to manage them, or set where LANCommander finds the host's data folder under Settings → Servers."
                : "The container isn't running. Start the server to manage its files.");
    }

    private static async Task<(string Output, string Error, long ExitCode)> RunAsync(DockerClient client, string container, string script, string[] arguments, Stream? input, CancellationToken cancellationToken)
    {
        var (execId, stream) = await StartAsync(client, container, script, arguments, stdin: input != null, cancellationToken);

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

    private static async Task<(string ExecId, MultiplexedStream Stream)> StartAsync(DockerClient client, string container, string script, string[] arguments, bool stdin, CancellationToken cancellationToken)
    {
        var exec = await client.Exec.CreateContainerExecAsync(container, new ContainerExecCreateParameters
        {
            AttachStdin = stdin,
            AttachStdout = true,
            AttachStderr = true,
            // The arguments are the script's $1, $2..., so they are never read as shell
            Cmd = ["sh", "-c", script, "sh", .. arguments],
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
