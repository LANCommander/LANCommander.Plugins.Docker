namespace LANCommander.Plugins.Docker.Hosts;

/// <summary>
/// Finds folders on a Docker host where LANCommander itself can reach them: inside the host's data
/// folder, at <see cref="DockerHost.LocalDataFolder"/>, or at the same path when LANCommander runs on
/// the Docker host itself.
/// </summary>
public static class HostFolders
{
    /// <summary>
    /// Where LANCommander finds the host's data folder; null when it can't. Without
    /// <see cref="DockerHost.LocalDataFolder"/>, the data folder's own path is used if LANCommander is
    /// on the same machine as the daemon (its address is a local socket or pipe) and the folder is there.
    /// </summary>
    public static string? LocalDataFolderOf(DockerHost host)
    {
        if (string.IsNullOrWhiteSpace(host.DataFolder))
            return null;

        if (!string.IsNullOrWhiteSpace(host.LocalDataFolder))
            return host.LocalDataFolder.Trim();

        var dataFolder = host.DataFolder.Trim();

        return IsLocal(host.Address) && Directory.Exists(dataFolder) ? dataFolder : null;
    }

    /// <summary>
    /// Where LANCommander finds a folder on the Docker host, such as a bind mount's source; null if
    /// it isn't inside the host's data folder, or LANCommander can't reach that.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">A link in the path leads out of the data folder.</exception>
    public static string? LocalPathOf(DockerHost host, string hostPath)
    {
        if (LocalDataFolderOf(host) is not { } local)
            return null;

        return RelativeTo(hostPath, host.DataFolder) is { } segments
            ? Confine(local, segments)
            : null;
    }

    /// <summary>A path's folders below <paramref name="parent"/>, on the Docker host's terms; null if it isn't in it.</summary>
    internal static IReadOnlyList<string>? RelativeTo(string path, string parent)
    {
        var folder = Normalize(path);
        var root = Normalize(parent);

        if (folder.Length == 0 || root.Length == 0)
            return null;

        // Windows paths don't care about case; Linux ones do
        var comparison = root.Length > 1 && root[1] == ':' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (string.Equals(folder, root, comparison))
            return [];

        if (!folder.StartsWith(root == "/" ? "/" : root + "/", comparison))
            return null;

        var segments = folder[(root == "/" ? 1 : root.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(s => s is ".." or ".") ? null : segments;
    }

    /// <summary>
    /// The path of <paramref name="segments"/> under <paramref name="root"/>, with the links along it
    /// followed, as the file system would follow them, but only as far as they stay under the root.
    /// What doesn't exist yet is taken as written.
    /// </summary>
    /// <remarks>
    /// A server can make links in its own folders; following one out of them would let it have
    /// LANCommander read or change its own files instead.
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">A link leads out of the root.</exception>
    /// <exception cref="IOException">The links go round in a circle.</exception>
    public static string Confine(string root, IEnumerable<string> segments)
    {
        root = Path.GetFullPath(root);

        var resolved = new List<string>();
        var pending = new LinkedList<string>(segments);
        var links = 0;

        while (pending.First is { } next)
        {
            pending.RemoveFirst();

            var segment = next.Value;

            if (segment is "" or ".")
                continue;

            if (segment == "..")
            {
                if (resolved.Count == 0)
                    throw new UnauthorizedAccessException("The path leads out of the server's folder.");

                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            var folder = Path.Combine([root, .. resolved]);
            var candidate = Path.Combine(folder, segment);

            string? target;

            try
            {
                target = new FileInfo(candidate).LinkTarget;
            }
            catch (IOException)
            {
                target = null;
            }

            if (target == null)
            {
                resolved.Add(segment);
                continue;
            }

            if (++links > 40)
                throw new IOException($"{candidate}: too many levels of links.");

            // The folder it's relative to has no links left in it, so ".." in it is taken as written
            var relative = Path.GetRelativePath(root, Path.GetFullPath(target, folder));

            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new UnauthorizedAccessException($"{candidate} links to {target}, outside the server's folder.");

            // Followed from the root again, as the target may have links of its own
            resolved.Clear();

            foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Reverse())
                pending.AddFirst(part);
        }

        return Path.Combine([root, .. resolved]);
    }

    /// <summary>Whether a daemon's address is on this machine: a Unix socket or a Windows pipe.</summary>
    public static bool IsLocal(string? address) =>
        Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "unix" or "npipe";

    private static string Normalize(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');

        return normalized == "/" ? normalized : normalized.TrimEnd('/');
    }
}
