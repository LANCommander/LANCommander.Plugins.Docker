namespace LANCommander.Plugins.Docker.Hosts;

/// <summary>The plugin's settings, kept by the server in the plugin's Settings.yaml.</summary>
public sealed class DockerSettings
{
    /// <summary>The Docker hosts game servers can run on.</summary>
    public List<DockerHost> Hosts { get; set; } = [];

    /// <summary>
    /// Whether the Docker hosts the server's built-in Docker engine used have been taken over, so a
    /// host removed since isn't brought back.
    /// </summary>
    public bool LegacyHostsImported { get; set; }

    /// <summary>
    /// Where to read an index of LANCommander's server images and their labels from, for titling and
    /// matching images the Docker host hasn't pulled; empty to read only the host's images' labels.
    /// See <see cref="Images.ImageLabels"/>.
    /// </summary>
    public string ImageCatalogUrl { get; set; } = "";
}

/// <summary>A Docker daemon the plugin connects to.</summary>
public sealed class DockerHost
{
    /// <summary>
    /// Identifies the host to the servers that run on it. Hosts taken over from the built-in engine
    /// keep the id they had there, which those servers name.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>
    /// The daemon's address, e.g. <c>unix:///var/run/docker.sock</c>, <c>npipe://./pipe/docker_engine</c>
    /// or <c>tcp://docker-host:2375</c>.
    /// </summary>
    public string Address { get; set; } = "";

    /// <summary>
    /// A folder on the Docker host new containers keep their data in, each in a folder of its name,
    /// e.g. <c>/data/Servers</c>. Empty for named Docker volumes.
    /// </summary>
    public string DataFolder { get; set; } = "";

    /// <summary>The address of the local daemon on this operating system.</summary>
    public static string DefaultAddress => OperatingSystem.IsWindows()
        ? "npipe://./pipe/docker_engine"
        : "unix:///var/run/docker.sock";
}
