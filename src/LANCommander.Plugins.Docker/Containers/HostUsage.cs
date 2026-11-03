namespace LANCommander.Plugins.Docker.Containers;

/// <summary>What the other containers on a Docker host take: ports, and folders or volumes.</summary>
public sealed record HostUsage(IReadOnlyList<PortUse> Ports, IReadOnlyList<MountUse> Mounts)
{
    public static readonly HostUsage Empty = new([], []);

    /// <summary>The other containers that mount the same folder or volume as <paramref name="mount"/>.</summary>
    public IReadOnlyList<MountUse> SharersOf(VolumeMount mount) =>
        string.IsNullOrWhiteSpace(mount.Source)
            ? []
            : Mounts.Where(use => SameSource(use.Source, mount.Source)).ToList();

    // Paths on the host are compared as written, but for a trailing separator
    private static bool SameSource(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/', '\\'), b.Trim().TrimEnd('/', '\\'), StringComparison.Ordinal);
}

/// <summary>A folder on a Docker host, or a volume, that a container mounts.</summary>
/// <param name="Running">Whether the container is running now.</param>
public sealed record MountUse(string Container, bool Running, string Source, string Destination, bool ReadOnly);
