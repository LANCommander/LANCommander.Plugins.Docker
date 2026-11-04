namespace LANCommander.Plugins.Docker.Containers;

/// <summary>Where a mount's files are: a Docker volume, or a folder on the Docker host.</summary>
public enum MountKind
{
    Volume,
    Folder,
}

/// <summary>A container's mount, and whether its files can be removed with the container.</summary>
/// <param name="IsGameFiles">The game files LANCommander's server images keep in <see cref="ContainerDefinition.BaseFolder"/>.</param>
/// <param name="IsAnonymous">A volume Docker made without a name, for an image's <c>VOLUME</c>, which nothing else would use.</param>
/// <param name="SharedWith">The other containers that mount it, running or not.</param>
/// <param name="CantRemove">Why its files can't be removed; null if they can.</param>
public sealed record MountRemoval(
    VolumeMount Mount,
    MountKind Kind,
    bool IsGameFiles,
    bool IsAnonymous,
    IReadOnlyList<string> SharedWith,
    string? CantRemove)
{
    public bool CanRemove => CantRemove == null;

    /// <summary>Whether it's offered for removal before being chosen: only volumes no one else could use.</summary>
    public bool RemoveByDefault => CanRemove && IsAnonymous;
}

/// <summary>What removing a container would do.</summary>
public sealed record RemovalPlan(string Container, bool Running, IReadOnlyList<MountRemoval> Mounts);

/// <summary>What removing a container did: the files removed with it, and those that couldn't be.</summary>
public sealed record ContainerRemoved(string Container, IReadOnlyList<string> Removed, IReadOnlyList<string> Failed);

/// <summary>Decides which of a container's files may be removed with it.</summary>
public static class ContainerRemoval
{
    /// <summary>
    /// Each of the mounts, and whether its files can be removed: not if another container mounts them,
    /// and for a folder on the host, only inside the host's data folder, as nothing else says a folder is
    /// the plugin's to remove.
    /// </summary>
    public static IReadOnlyList<MountRemoval> Plan(IReadOnlyList<VolumeMount> mounts, HostUsage others, string? dataFolder) =>
        mounts
            .Where(m => !string.IsNullOrWhiteSpace(m.Source))
            .Select(mount =>
            {
                var kind = IsFolder(mount.Source) ? MountKind.Folder : MountKind.Volume;
                var sharedWith = others.SharersOf(mount).Select(s => s.Running ? s.Container : $"{s.Container} (stopped)").Distinct().ToList();

                string? cantRemove = null;

                if (sharedWith.Count > 0)
                    cantRemove = $"Also used by {string.Join(", ", sharedWith)}.";
                else if (kind == MountKind.Folder && string.IsNullOrWhiteSpace(dataFolder))
                    cantRemove = "A folder on the host is only removed inside the host's data folder, and the host has none.";
                else if (kind == MountKind.Folder && !IsInside(mount.Source, dataFolder!))
                    cantRemove = $"A folder on the host is only removed inside the host's data folder, {dataFolder!.Trim()}.";

                return new MountRemoval(
                    mount,
                    kind,
                    mount.Destination.Trim().TrimEnd('/') == ContainerDefinition.BaseFolder,
                    kind == MountKind.Volume && IsAnonymous(mount.Source),
                    sharedWith,
                    cantRemove);
            })
            .ToList();

    /// <summary>A path on the Docker host, rather than a volume's name: <c>/srv/game</c>, <c>D:\Servers</c>.</summary>
    public static bool IsFolder(string source)
    {
        var path = source.Trim();

        return path.StartsWith('/') || path.StartsWith('\\') || (path.Length > 1 && path[1] == ':');
    }

    /// <summary>The name Docker gives a volume it makes without one: 64 hex digits.</summary>
    public static bool IsAnonymous(string volume) =>
        volume.Trim().Length == 64 && volume.Trim().All(char.IsAsciiHexDigitLower);

    /// <summary>Whether the folder is inside the other, and not the same folder.</summary>
    public static bool IsInside(string folder, string parent)
    {
        var path = Normalize(folder);
        var root = Normalize(parent);

        if (path.Length == 0 || root.Length == 0 || path.Split('/').Any(segment => segment is ".." or "."))
            return false;

        // Windows paths don't care about case; Linux ones do
        var comparison = root.Length > 1 && root[1] == ':' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return path.StartsWith(root + "/", comparison) && path.Length > root.Length + 1;
    }

    /// <summary>The folder a folder is in, and its name in it, on the host's own terms.</summary>
    public static (string Parent, string Name) Split(string folder)
    {
        var path = folder.Trim().TrimEnd('/', '\\');
        var separator = path.LastIndexOfAny(['/', '\\']);

        if (separator < 0)
            throw new ArgumentException($"{folder} isn't a folder on the host.", nameof(folder));

        var parent = path[..separator];

        // The root of a drive or the file system
        if (parent.Length == 0)
            parent = path[..1];
        else if (parent.Length == 2 && parent[1] == ':')
            parent += path[separator];

        return (parent, path[(separator + 1)..]);
    }

    private static string Normalize(string path) =>
        path.Trim().Replace('\\', '/').TrimEnd('/');
}
