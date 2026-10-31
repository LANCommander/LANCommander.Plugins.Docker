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
}
