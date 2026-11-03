namespace LANCommander.Plugins.Docker.Engine;

/// <summary>
/// What the Docker engine keeps about each server, in the server's engine settings. Everything else
/// about the container is read from the container itself.
/// </summary>
public sealed class DockerServerSettings
{
    /// <summary>The <see cref="Hosts.DockerHost"/> the container is on.</summary>
    public Guid? HostId { get; set; }

    /// <summary>
    /// The container's name, or its id for servers set up before the plugin, which stored the id.
    /// Docker takes either.
    /// </summary>
    public string ContainerId { get; set; } = "";

    /// <summary>
    /// The folder in the container the server's Files page shows, e.g. <c>/data</c>. Empty for the
    /// container's working directory.
    /// </summary>
    public string? FilesPath { get; set; }

    /// <summary>Log files in the container to show as consoles beside its own log, as the server writes them.</summary>
    public List<ContainerLogFile> LogFiles { get; set; } = [];
}

/// <summary>A log file the server writes in its container.</summary>
public sealed class ContainerLogFile
{
    /// <summary>What its console is called, e.g. <c>Server log</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Where it is in the container, e.g. <c>/config/Overlay/System/server.log</c>. A stopped
    /// container's files are only read where they're on disk: in a volume, not in an overlay that's
    /// only mounted while it runs.
    /// </summary>
    public string Path { get; set; } = "";
}
