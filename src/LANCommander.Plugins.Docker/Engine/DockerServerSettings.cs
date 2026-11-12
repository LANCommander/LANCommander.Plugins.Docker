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
    /// Log files in the container to show as consoles beside its own log, as the server writes them,
    /// besides those its image names (<see cref="ContainerLogFile.FromLabels"/>).
    /// </summary>
    public List<ContainerLogFile> LogFiles { get; set; } = [];
}

/// <summary>A log file the server writes in its container.</summary>
public sealed class ContainerLogFile
{
    /// <summary>
    /// An image names its log files with a label each: this, a name, and the file's path as its value,
    /// e.g. <c>app.lancommander.server.log.server-log=/config/Overlay/System/server.log</c>.
    /// </summary>
    public const string LabelPrefix = "app.lancommander.server.log.";

    /// <summary>What its console is called, e.g. <c>Server log</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Where it is in the container, e.g. <c>/config/Overlay/System/server.log</c>. A stopped
    /// container's files are only read where they're on disk: in a volume, not in an overlay that's
    /// only mounted while it runs.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>The log files a container's labels, or its image's, name, in the order of their names.</summary>
    public static IReadOnlyList<ContainerLogFile> FromLabels(IDictionary<string, string>? labels) =>
        (labels ?? new Dictionary<string, string>())
            .Where(l => l.Key.StartsWith(LabelPrefix, StringComparison.OrdinalIgnoreCase) && l.Key.Length > LabelPrefix.Length && !string.IsNullOrWhiteSpace(l.Value))
            .OrderBy(l => l.Key, StringComparer.OrdinalIgnoreCase)
            .Select(l => new ContainerLogFile { Name = TitleOf(l.Key[LabelPrefix.Length..]), Path = l.Value.Trim() })
            .DistinctBy(f => f.Path)
            .ToList();

    // server-log is "Server log"
    private static string TitleOf(string name)
    {
        var words = name.Trim().Replace('-', ' ').Replace('_', ' ').Replace('.', ' ');

        return words.Length == 0 ? name : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
