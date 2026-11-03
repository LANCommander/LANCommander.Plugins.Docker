namespace LANCommander.Plugins.Docker.Containers;

/// <summary>How a container's network puts its ports on the Docker host.</summary>
public enum NetworkKind
{
    /// <summary>Behind Docker's NAT: only published ports are on the host, as their host ports.</summary>
    Bridge,

    /// <summary>The host's own network: every port the server listens on is a port of the host's.</summary>
    Host,

    /// <summary>An address of its own, as on a macvlan or ipvlan network: nothing it uses is the host's.</summary>
    OwnAddress,

    /// <summary>No network, or another container's: it takes no ports of its own.</summary>
    None,
}

/// <summary>A port on a Docker host that a container takes, or will take when it runs.</summary>
/// <param name="Container">The container's name.</param>
/// <param name="Running">Whether it's running now; a stopped one takes the port when started.</param>
/// <param name="HostIp">The host address it's on; empty for every address.</param>
public sealed record PortUse(string Container, bool Running, string Protocol, string HostIp, int Port);

/// <summary>A port of a container's that another may want too.</summary>
public sealed record PortConflict(PortMapping Port, string Message);

/// <summary>
/// Finds the ports a container would share with others on its Docker host. Only what Docker knows of
/// is seen: other containers, running or not. Programs on the host itself aren't.
/// </summary>
public static class PortConflicts
{
    /// <summary>How a network puts ports on the host.</summary>
    /// <param name="network">A container's network mode; empty for Docker's default.</param>
    /// <param name="drivers">The host's networks' drivers by name, for networks of its own.</param>
    public static NetworkKind KindOf(string? network, IReadOnlyDictionary<string, string> drivers)
    {
        network = network?.Trim() ?? "";

        if (network is "" or "default" or "bridge")
            return NetworkKind.Bridge;

        if (network == "host")
            return NetworkKind.Host;

        if (network == "none" || network.StartsWith("container:", StringComparison.Ordinal))
            return NetworkKind.None;

        // An unknown network is taken as a bridge, to warn rather than miss a conflict
        return drivers.TryGetValue(network, out var driver) ? driver switch
        {
            "host" => NetworkKind.Host,
            "macvlan" or "ipvlan" => NetworkKind.OwnAddress,
            "null" => NetworkKind.None,
            _ => NetworkKind.Bridge,
        } : NetworkKind.Bridge;
    }

    /// <summary>The host ports a container's ports take on its network, with the port each is for.</summary>
    public static IEnumerable<(PortMapping Port, string HostIp, int HostPort)> HostPortsOf(IEnumerable<PortMapping> ports, NetworkKind kind) => kind switch
    {
        NetworkKind.Bridge => ports
            .Where(port => port.HostPort is > 0)
            .Select(port => (port, port.HostIp?.Trim() ?? "", port.HostPort!.Value)),

        // Published ports mean nothing here: the server listens on the host's port itself
        NetworkKind.Host => ports
            .Where(port => port.ContainerPort > 0)
            .Select(port => (port, "", port.ContainerPort)),

        _ => [],
    };

    /// <summary>What a container's ports take on the host, for comparing others with.</summary>
    public static IEnumerable<PortUse> UsesOf(string container, bool running, IEnumerable<PortMapping> ports, NetworkKind kind) =>
        HostPortsOf(ports, kind).Select(p => new PortUse(container, running, Protocol(p.Port), p.HostIp, p.HostPort));

    /// <summary>
    /// The container's ports that others on the host take too, or that it takes twice. Nothing on a
    /// network with addresses of its own conflicts.
    /// </summary>
    public static IReadOnlyList<PortConflict> Find(IReadOnlyList<PortMapping> ports, NetworkKind kind, IEnumerable<PortUse> others)
    {
        var mine = HostPortsOf(ports, kind).ToList();
        var theirs = others.ToList();
        var conflicts = new List<PortConflict>();

        foreach (var (port, hostIp, hostPort) in mine)
        {
            var clashes = theirs
                .Where(use => Overlaps(Protocol(port), hostIp, hostPort, use.Protocol, use.HostIp, use.Port))
                .Select(use => use.Running ? use.Container : $"{use.Container} (stopped)")
                .Distinct()
                .ToList();

            var twice = mine.Count(other => !ReferenceEquals(other.Port, port) && Overlaps(Protocol(port), hostIp, hostPort, Protocol(other.Port), other.HostIp, other.HostPort)) > 0;

            if (clashes.Count == 0 && !twice)
                continue;

            var what = $"{hostPort}/{Protocol(port)}";
            var messages = new List<string>();

            if (clashes.Count > 0)
                messages.Add($"The host's port {what} is also taken by {string.Join(", ", clashes)}. They can't run at the same time.");

            if (twice)
                messages.Add($"The host's port {what} is given to more than one of this container's ports.");

            conflicts.Add(new PortConflict(port, string.Join(" ", messages)));
        }

        return conflicts;
    }

    /// <summary>
    /// The smallest amount to move every published host port up by, keeping them the same distance
    /// apart as games that work out one port from another expect, so none conflicts; null if there's
    /// none under <paramref name="limit"/>, or the network doesn't publish ports.
    /// </summary>
    public static int? FreeOffset(IReadOnlyList<PortMapping> ports, NetworkKind kind, IEnumerable<PortUse> others, int limit = 1000)
    {
        if (kind != NetworkKind.Bridge)
            return null;

        var theirs = others.ToList();

        for (var offset = 0; offset < limit; offset++)
        {
            var moved = ports
                .Select(port => new PortMapping
                {
                    ContainerPort = port.ContainerPort,
                    Protocol = port.Protocol,
                    HostIp = port.HostIp,
                    HostPort = port.HostPort + offset,
                })
                .ToList();

            if (moved.Any(port => port.HostPort > 65535))
                return null;

            if (Find(moved, kind, theirs).Count == 0)
                return offset;
        }

        return null;
    }

    private static string Protocol(PortMapping port) => port.Protocol == "tcp" ? "tcp" : "udp";

    private static bool Overlaps(string protocol, string hostIp, int port, string otherProtocol, string otherHostIp, int otherPort) =>
        protocol == otherProtocol
        && port == otherPort
        && (IsEveryAddress(hostIp) || IsEveryAddress(otherHostIp) || string.Equals(hostIp.Trim(), otherHostIp.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsEveryAddress(string hostIp) => hostIp.Trim() is "" or "0.0.0.0" or "::";
}
