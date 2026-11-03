using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;

namespace LANCommander.Plugins.Docker.Containers;

/// <summary>
/// What an administrator can change about a game server's container. Read from the container
/// itself, so there is nothing to keep in step with it; applying a change recreates the container,
/// since Docker can't change these on one that exists.
/// </summary>
/// <remarks>
/// A container's configuration as Docker reports it is the image's with the container's own on top.
/// Read with the image it was created from, only what differs from the image is part of the
/// definition, so a container recreated from a newer image picks up that image's defaults rather
/// than keeping the old image's.
/// </remarks>
public sealed class ContainerDefinition
{
    /// <summary>The container's name, without Docker's leading slash.</summary>
    public string Name { get; set; } = "";

    /// <summary>The image, e.g. <c>itzg/minecraft-server:latest</c>.</summary>
    public string Image { get; set; } = "";

    /// <summary>The arguments given to the image's entry point, as a command line. Empty uses the image's.</summary>
    public string Command { get; set; } = "";

    public List<EnvironmentVariable> Environment { get; set; } = [];

    public List<PortMapping> Ports { get; set; } = [];

    public List<VolumeMount> Mounts { get; set; } = [];

    /// <summary>One of <see cref="RestartPolicies"/>.</summary>
    public string RestartPolicy { get; set; } = "no";

    /// <summary>Keeps the container's standard input open, so commands can be typed into it.</summary>
    public bool Interactive { get; set; } = true;

    /// <summary>Gives the container a terminal. Its output is then one stream rather than two.</summary>
    public bool Terminal { get; set; }

    /// <summary>The user the entry point runs as, e.g. <c>root</c> or <c>1000:1000</c>. Empty uses the image's.</summary>
    public string User { get; set; } = "";

    /// <summary>Linux capabilities added to the container's, e.g. <c>SYS_ADMIN</c>.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Docker's security options for the container, e.g. <c>apparmor:unconfined</c>.</summary>
    public List<string> SecurityOptions { get; set; } = [];

    /// <summary>
    /// The network the container joins: <c>bridge</c>, <c>host</c>, or one of the host's own, such as a
    /// macvlan network giving it an address on the LAN. Empty for Docker's default bridge.
    /// </summary>
    public string Network { get; set; } = "";

    /// <summary>A fixed IPv4 address on <see cref="Network"/>; empty for one the network assigns.</summary>
    public string IPAddress { get; set; } = "";

    /// <summary>
    /// What the image provides where the definition doesn't say, for showing alongside it; null when
    /// the container was read without its image. Not applied: Docker applies the image's itself.
    /// </summary>
    public ImageDefaults? Defaults { get; init; }

    public static readonly IReadOnlyList<string> RestartPolicies = ["no", "on-failure", "unless-stopped", "always"];

    /// <summary>
    /// Whether the container may mount file systems, as LANCommander's server images do to lay an
    /// overlay over the server's files: with <c>SYS_ADMIN</c>, and without AppArmor's profile, which
    /// forbids mounts even then. Setting it adds or removes both, leaving any others.
    /// </summary>
    public bool CanMountOverlay
    {
        get => Capabilities.Any(IsSysAdmin) && SecurityOptions.Any(IsAppArmorUnconfined);
        set
        {
            Capabilities.RemoveAll(IsSysAdmin);
            SecurityOptions.RemoveAll(IsAppArmorUnconfined);

            if (value)
            {
                Capabilities.Add("SYS_ADMIN");
                SecurityOptions.Add("apparmor:unconfined");
            }
        }
    }

    /// <summary>The capabilities and security options besides those <see cref="CanMountOverlay"/> stands for.</summary>
    public IEnumerable<string> OtherPrivileges => CanMountOverlay
        ? Capabilities.Where(c => !IsSysAdmin(c)).Concat(SecurityOptions.Where(o => !IsAppArmorUnconfined(o)))
        : Capabilities.Concat(SecurityOptions);

    private static bool IsSysAdmin(string capability) =>
        capability.Trim().Equals("SYS_ADMIN", StringComparison.OrdinalIgnoreCase) || capability.Trim().Equals("CAP_SYS_ADMIN", StringComparison.OrdinalIgnoreCase);

    // Docker takes both forms; ':' is the older
    private static bool IsAppArmorUnconfined(string option) =>
        option.Trim().Replace('=', ':').Equals("apparmor:unconfined", StringComparison.OrdinalIgnoreCase);

    /// <param name="container">The container, as Docker reports it.</param>
    /// <param name="image">
    /// The image it was created from (by its id, <see cref="ContainerInspectResponse.Image"/>), to
    /// leave out what the container only has from it. Without it, everything the container has is
    /// part of the definition, and a recreated container keeps it.
    /// </param>
    public static ContainerDefinition From(ContainerInspectResponse container, ImageInspectResponse? image = null)
    {
        var config = container.Config ?? new ContainerConfig();
        var host = container.HostConfig ?? new HostConfig();
        var defaults = image?.Config;

        var imageEnvironment = new HashSet<string>(defaults?.Env ?? [], StringComparer.Ordinal);

        // Docker drops the image's command when a container has an entry point of its own, so the
        // command is only the image's if the entry point is too
        var imageCommand = defaults != null && SameList(config.Entrypoint, defaults.Entrypoint) && SameList(config.Cmd, defaults.Cmd);

        return new ContainerDefinition
        {
            Name = container.Name?.TrimStart('/') ?? "",
            Image = config.Image ?? "",
            Command = imageCommand ? "" : ShellWords.Join(config.Cmd),
            Environment = (config.Env ?? [])
                .Where(variable => !imageEnvironment.Contains(variable))
                .Select(EnvironmentVariable.Parse)
                .ToList(),
            Ports = (host.PortBindings ?? new Dictionary<string, IList<PortBinding>>())
                .SelectMany(binding => (binding.Value is { Count: > 0 } ? binding.Value : [new PortBinding()])
                    .Select(hostPort => PortMapping.Parse(binding.Key, hostPort)))
                .Concat((config.ExposedPorts ?? new Dictionary<string, EmptyStruct>()).Keys
                    .Where(port => host.PortBindings?.ContainsKey(port) != true)
                    .Select(port => PortMapping.Parse(port, null)))
                .OrderBy(p => p.ContainerPort)
                .ThenBy(p => p.Protocol)
                .ToList(),
            Mounts = (container.Mounts ?? [])
                .Where(mount => mount.Type is "bind" or "volume")
                .Select(mount => new VolumeMount
                {
                    Source = mount.Type == "volume" ? mount.Name : mount.Source,
                    Destination = mount.Destination,
                    ReadOnly = !mount.RW,
                })
                .OrderBy(m => m.Destination, StringComparer.Ordinal)
                .ToList(),
            RestartPolicy = host.RestartPolicy?.Name switch
            {
                RestartPolicyKind.Always => "always",
                RestartPolicyKind.UnlessStopped => "unless-stopped",
                RestartPolicyKind.OnFailure => "on-failure",
                _ => "no",
            },
            Interactive = config.OpenStdin,
            Terminal = config.Tty,
            User = defaults != null && (config.User ?? "") == (defaults.User ?? "") ? "" : config.User ?? "",
            Capabilities = (host.CapAdd ?? []).ToList(),
            SecurityOptions = (host.SecurityOpt ?? []).ToList(),
            Network = host.NetworkMode is null or "default" ? "" : host.NetworkMode,
            IPAddress = host.NetworkMode != null && container.NetworkSettings?.Networks?.TryGetValue(host.NetworkMode, out var endpoint) == true
                ? endpoint.IPAMConfig?.IPv4Address ?? ""
                : "",
            Defaults = defaults == null ? null : new ImageDefaults
            {
                Environment = (defaults.Env ?? []).Select(EnvironmentVariable.Parse).ToList(),
                Command = ShellWords.Join(defaults.Cmd),
                User = defaults.User ?? "",
            },
        };
    }

    /// <summary>
    /// What to create the container from. With <paramref name="basis"/>, the container this one
    /// replaces, everything the spec doesn't cover is kept from it: entry point, network, limits and
    /// so on. With <paramref name="basisImage"/>, the image <paramref name="basis"/> was created from,
    /// what it only had from that image is left to the new container's image instead.
    /// </summary>
    public CreateContainerParameters ToCreateParameters(ContainerInspectResponse? basis = null, ImageInspectResponse? basisImage = null)
    {
        Validate();

        var parameters = basis?.Config != null ? new CreateContainerParameters(basis.Config) : new CreateContainerParameters();
        var host = basis?.HostConfig ?? new HostConfig();

        // A hostname Docker made up from the old container's id would name the wrong container
        if (basis?.ID != null && parameters.Hostname != null && basis.ID.StartsWith(parameters.Hostname, StringComparison.Ordinal))
            parameters.Hostname = null;

        if (basisImage?.Config is { } old)
            LeaveToImage(parameters, old);

        parameters.Name = Name.Trim();
        parameters.Image = Image.Trim();
        parameters.User = string.IsNullOrWhiteSpace(User) ? null : User.Trim();
        parameters.Cmd = string.IsNullOrWhiteSpace(Command) ? null : ShellWords.Split(Command);
        parameters.Env = Environment
            .Where(variable => !string.IsNullOrWhiteSpace(variable.Name))
            .Select(variable => $"{variable.Name.Trim()}={variable.Value}")
            .ToList();
        parameters.ExposedPorts = Ports
            .Select(port => port.Key)
            .Distinct()
            .ToDictionary(key => key, _ => default(EmptyStruct));
        parameters.OpenStdin = Interactive;
        parameters.AttachStdin = Interactive;
        parameters.StdinOnce = false;
        parameters.Tty = Terminal;

        host.PortBindings = Ports
            .Where(port => port.HostPort != null)
            .GroupBy(port => port.Key)
            .ToDictionary(group => group.Key, group => (IList<PortBinding>)group
                .Select(port => new PortBinding { HostIP = port.HostIp?.Trim() ?? "", HostPort = port.HostPort!.Value.ToString() })
                .ToList());

        // Every mount, including the anonymous volumes Docker made for the old container, is named
        // here, so the new container gets the same data rather than empty volumes of its own
        host.Binds = Mounts
            .Where(mount => !string.IsNullOrWhiteSpace(mount.Source) && !string.IsNullOrWhiteSpace(mount.Destination))
            .Select(mount => $"{mount.Source.Trim()}:{mount.Destination.Trim()}{(mount.ReadOnly ? ":ro" : "")}")
            .ToList();
        host.Mounts = null;

        host.RestartPolicy = new RestartPolicy
        {
            Name = RestartPolicy switch
            {
                "always" => RestartPolicyKind.Always,
                "unless-stopped" => RestartPolicyKind.UnlessStopped,
                "on-failure" => RestartPolicyKind.OnFailure,
                _ => RestartPolicyKind.No,
            },
        };

        host.CapAdd = Capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Select(capability => capability.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        host.SecurityOpt = SecurityOptions
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Select(option => option.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        host.NetworkMode = string.IsNullOrWhiteSpace(Network) ? null : Network.Trim();

        // Docker doesn't keep a fixed address unless it's asked for again
        parameters.NetworkingConfig = string.IsNullOrWhiteSpace(IPAddress)
            ? null
            : new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [Network.Trim()] = new EndpointSettings { IPAMConfig = new EndpointIPAMConfig { IPv4Address = IPAddress.Trim() } },
                },
            };

        parameters.HostConfig = host;

        return parameters;
    }

    /// <summary>Where LANCommander's server images keep a server's own files: its overlay, scripts and so on.</summary>
    public const string ConfigFolder = "/config";

    /// <summary>
    /// Where LANCommander's server images keep the game's files: the overlay's lower layer, which the
    /// server only changes through its overlay, so servers of the same game can share it.
    /// </summary>
    public const string BaseFolder = "/config/Server";

    /// <summary>The folder a game's shared files are kept in, beside its servers' own.</summary>
    public const string BaseFolderName = "Base";

    /// <summary>
    /// A definition for a new container from an image, as far as the image tells: its ports published
    /// on the same host ports, its volumes kept under <paramref name="dataFolder"/>, and for one of
    /// LANCommander's server images, its <c>/config</c> folder and the privileges it needs.
    /// </summary>
    /// <remarks>
    /// A game's server image gets the game's files from a folder shared by every server of the game,
    /// mounted as <see cref="BaseFolder"/>: the image lays an overlay of the server's own over them,
    /// so a deathmatch and a CTF server keep one copy of the game, each with its own configuration.
    /// </remarks>
    /// <param name="dataFolder">
    /// A folder on the Docker host to keep the container's volumes in: a game's in a folder of the
    /// game's name, holding its shared files and a folder for each server; another image's in a folder
    /// of the container's name. Without one, they're named Docker volumes.
    /// </param>
    public static ContainerDefinition ForImage(string name, string image, ImageInspectResponse inspected, string? dataFolder)
    {
        var config = inspected.Config ?? new DockerOCIImageConfig();
        var serverImage = IsServerImage(image);
        var game = SharesGameFiles(image) ? GameOf(image) : null;

        var volumes = (config.Volumes?.Keys ?? [])
            .Concat(serverImage ? [ConfigFolder] : [])
            .Select(path => path.TrimEnd('/'))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var mounts = volumes
            .Select(path => new VolumeMount { Source = VolumeSource(name, game, path, volumes.Count, dataFolder), Destination = path })
            .ToList();

        if (game != null && !volumes.Contains(BaseFolder))
            mounts.Add(new VolumeMount { Source = BaseSource(game, dataFolder), Destination = BaseFolder });

        // Shared game files mustn't change under another server's overlay, so a server doesn't update
        // them when it starts; Update Game Files does, with them all stopped
        List<EnvironmentVariable> environment = game != null && (config.Env ?? []).Any(v => v.StartsWith(UpdateVariable + "=", StringComparison.Ordinal))
            ? [new EnvironmentVariable { Name = UpdateVariable, Value = "false" }]
            : [];

        var spec = new ContainerDefinition
        {
            Name = name,
            Image = image,
            Ports = (config.ExposedPorts?.Keys ?? [])
                .Select(key => PortMapping.Parse(key, null))
                .Where(port => port.ContainerPort > 0)
                .Select(port =>
                {
                    port.HostPort = port.ContainerPort;
                    return port;
                })
                .OrderBy(p => p.ContainerPort)
                .ThenBy(p => p.Protocol)
                .ToList(),
            Mounts = mounts,
            Environment = environment,
            RestartPolicy = serverImage ? "unless-stopped" : "no",
            User = serverImage ? "root" : "",
            Defaults = new ImageDefaults
            {
                Environment = (config.Env ?? []).Select(EnvironmentVariable.Parse).ToList(),
                Command = ShellWords.Join(config.Cmd),
                User = config.User ?? "",
            },
        };

        spec.CanMountOverlay = serverImage;

        return spec;
    }

    /// <summary>The game files folder the container mounts as <see cref="BaseFolder"/>, if it does.</summary>
    public VolumeMount? GameFiles => Mounts.FirstOrDefault(m => m.Destination.Trim().TrimEnd('/') == BaseFolder && !string.IsNullOrWhiteSpace(m.Source));

    /// <summary>Whether a SteamCMD image installs and updates its game when it starts.</summary>
    public const string UpdateVariable = "STEAM_APP_UPDATE";

    // Set on a one-off container so the image installs or updates the game, then exits rather than
    // starting the server: START_EXE runs after the image's install hooks, and `true` exits at once
    private static readonly Dictionary<string, string> UpdateOnly = new()
    {
        ["START_EXE"] = "true",
        ["START_ARGS"] = "",
        [UpdateVariable] = "true",
        ["HTTP_FILESERVER_ENABLED"] = "false",
    };

    /// <summary>
    /// A one-off container like this server's that runs the image's install and update step on its
    /// files, then exits without starting the server. It publishes no ports and joins Docker's default
    /// network, so it can run beside anything and still reach the internet.
    /// </summary>
    public ContainerDefinition ForUpdatingGameFiles(string name) => new()
    {
        Name = name,
        Image = Image,
        Command = Command,
        Environment = Environment
            .Where(v => !UpdateOnly.ContainsKey(v.Name.Trim()))
            .Select(v => new EnvironmentVariable { Name = v.Name, Value = v.Value })
            .Concat(UpdateOnly.Select(v => new EnvironmentVariable { Name = v.Key, Value = v.Value }))
            .ToList(),
        Mounts = Mounts.Select(m => new VolumeMount { Source = m.Source, Destination = m.Destination, ReadOnly = m.ReadOnly }).ToList(),
        RestartPolicy = "no",
        Interactive = false,
        Terminal = false,
        User = User,
        Capabilities = [.. Capabilities],
        SecurityOptions = [.. SecurityOptions],
        Defaults = Defaults,
    };

    /// <summary>
    /// Where a new container's volume is kept: a folder under the data folder, in the game's folder for
    /// a game's server, or a named volume.
    /// </summary>
    private static string VolumeSource(string name, string? game, string destination, int volumes, string? dataFolder)
    {
        var leaf = SafeName(destination);

        if (string.IsNullOrWhiteSpace(dataFolder))
            return $"{name}-{leaf}";

        var separator = SeparatorOf(dataFolder);
        var root = dataFolder.Trim().TrimEnd('/', '\\');
        var folder = game == null ? root + separator + name : root + separator + game + separator + name;

        return volumes == 1 ? folder : folder + separator + leaf;
    }

    /// <summary>Where a game's files are kept for every server of it to share.</summary>
    private static string BaseSource(string game, string? dataFolder) =>
        string.IsNullOrWhiteSpace(dataFolder)
            ? $"{game}-{BaseFolderName.ToLowerInvariant()}"
            : dataFolder.Trim().TrimEnd('/', '\\') + SeparatorOf(dataFolder) + game + SeparatorOf(dataFolder) + BaseFolderName;

    // The Docker host's own separator; a Windows path has backslashes
    private static char SeparatorOf(string folder) => folder.Contains('\\') ? '\\' : '/';

    private static string SafeName(string path)
    {
        var name = new string(path.Trim('/').Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-').ToArray()).Trim('-');

        return name.Length == 0 ? "data" : name;
    }

    /// <summary>One of LANCommander's server images, which mount an overlay as root before starting the server as their own user.</summary>
    public static bool IsServerImage(string image) =>
        image.Trim().StartsWith("lancommander/", StringComparison.OrdinalIgnoreCase)
        || image.Trim().StartsWith("docker.io/lancommander/", StringComparison.OrdinalIgnoreCase);

    // The images games are built on, which run whichever game they're told to, so their servers
    // don't have the same game's files to share
    private static readonly HashSet<string> GenericImages = new(StringComparer.OrdinalIgnoreCase) { "base", "steamcmd", "wine" };

    /// <summary>
    /// Whether the image is a LANCommander image of one game, whose servers share the game's files: not
    /// one of the images games are built on.
    /// </summary>
    public static bool SharesGameFiles(string image) => IsServerImage(image) && !GenericImages.Contains(GameOf(image));

    /// <summary>The game an image is for, as its repository's name: <c>lancommander/quake3:1.0</c> is <c>quake3</c>.</summary>
    public static string GameOf(string image)
    {
        var (repository, _) = ContainerService.SplitImage(image.Trim());

        return SafeName(repository.Split('@')[0].Split('/')[^1].ToLowerInvariant());
    }

    /// <summary>Clears what the old container had only from its image, so the new one's image decides.</summary>
    private static void LeaveToImage(CreateContainerParameters parameters, DockerOCIImageConfig image)
    {
        if (SameList(parameters.Entrypoint, image.Entrypoint))
            parameters.Entrypoint = null;

        if ((parameters.WorkingDir ?? "") == (image.WorkingDir ?? ""))
            parameters.WorkingDir = null;

        if ((parameters.StopSignal ?? "") == (image.StopSignal ?? ""))
            parameters.StopSignal = null;

        if (SameList(parameters.Shell, image.Shell))
            parameters.Shell = null;

        if (SameList(parameters.OnBuild, image.OnBuild))
            parameters.OnBuild = null;

        if (parameters.Healthcheck != null && JsonSerializer.Serialize(parameters.Healthcheck) == JsonSerializer.Serialize(image.Healthcheck))
            parameters.Healthcheck = null;

        // Labels and volumes are merged with the image's, so only the container's own are kept. The
        // image's volumes are still mounted: every mount is named in the binds
        if (parameters.Labels != null && image.Labels != null)
        {
            parameters.Labels = parameters.Labels
                .Where(label => !(image.Labels.TryGetValue(label.Key, out var value) && value == label.Value))
                .ToDictionary(label => label.Key, label => label.Value);
        }

        if (parameters.Volumes != null && image.Volumes != null)
        {
            parameters.Volumes = parameters.Volumes
                .Where(volume => !image.Volumes.ContainsKey(volume.Key))
                .ToDictionary(volume => volume.Key, volume => volume.Value);
        }
    }

    private static bool SameList(IList<string>? a, IList<string>? b) =>
        (a ?? []).SequenceEqual(b ?? [], StringComparer.Ordinal);

    /// <exception cref="ArgumentException">Something the container can't be created with.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("The container needs a name.");

        if (Name.Trim().Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')) || !char.IsAsciiLetterOrDigit(Name.Trim()[0]))
            throw new ArgumentException("A container's name can only have letters, digits, '_', '.' and '-', and must start with a letter or digit.");

        if (string.IsNullOrWhiteSpace(Image))
            throw new ArgumentException("The container needs an image.");

        foreach (var port in Ports)
        {
            if (port.ContainerPort is < 1 or > 65535 || port.HostPort is < 1 or > 65535)
                throw new ArgumentException($"{port} isn't a port from 1 to 65535.");
        }

        if (!string.IsNullOrWhiteSpace(IPAddress))
        {
            if (!System.Net.IPAddress.TryParse(IPAddress.Trim(), out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new ArgumentException($"\"{IPAddress}\" isn't an IPv4 address.");

            if (Network.Trim() is "" or "bridge" or "host" or "none" or "default")
                throw new ArgumentException("A fixed address needs a network of the host's own, such as a macvlan network; Docker's bridge and host networks can't give one.");
        }

        foreach (var variable in Environment.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
        {
            if (variable.Name.Contains('='))
                throw new ArgumentException($"The environment variable \"{variable.Name}\" can't have '=' in its name.");
        }
    }
}

/// <summary>What a container's image sets, which the container has unless it sets otherwise.</summary>
public sealed class ImageDefaults
{
    public IReadOnlyList<EnvironmentVariable> Environment { get; init; } = [];

    /// <summary>The image's command, as a command line; empty if it has none.</summary>
    public string Command { get; init; } = "";

    /// <summary>The user the image runs as; empty for root.</summary>
    public string User { get; init; } = "";
}

public sealed class EnvironmentVariable
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    public static EnvironmentVariable Parse(string variable)
    {
        var separator = variable.IndexOf('=');

        return separator < 0
            ? new EnvironmentVariable { Name = variable }
            : new EnvironmentVariable { Name = variable[..separator], Value = variable[(separator + 1)..] };
    }
}

/// <summary>A port the container listens on, and the host port it is published on, if any.</summary>
public sealed class PortMapping
{
    public int ContainerPort { get; set; }

    /// <summary><c>tcp</c> or <c>udp</c>.</summary>
    public string Protocol { get; set; } = "udp";

    /// <summary>The host address it is published on; empty for every address.</summary>
    public string? HostIp { get; set; }

    /// <summary>The host port it is published on; null if it isn't published.</summary>
    public int? HostPort { get; set; }

    /// <summary>Docker's name for the container port, e.g. <c>27015/udp</c>.</summary>
    public string Key => $"{ContainerPort}/{(Protocol == "tcp" ? "tcp" : "udp")}";

    public static PortMapping Parse(string key, PortBinding? binding)
    {
        var parts = key.Split('/');

        return new PortMapping
        {
            ContainerPort = int.TryParse(parts[0], out var port) ? port : 0,
            Protocol = parts.Length > 1 ? parts[1] : "tcp",
            HostIp = string.IsNullOrWhiteSpace(binding?.HostIP) ? null : binding.HostIP,
            HostPort = int.TryParse(binding?.HostPort, out var hostPort) ? hostPort : null,
        };
    }

    public override string ToString() => HostPort == null ? Key : $"{HostPort}:{Key}";
}

/// <summary>A host folder or named volume mounted into the container.</summary>
public sealed class VolumeMount
{
    /// <summary>A folder on the Docker host, or a volume's name.</summary>
    public string Source { get; set; } = "";

    /// <summary>Where it appears in the container.</summary>
    public string Destination { get; set; } = "";

    public bool ReadOnly { get; set; }
}

/// <summary>Splits and joins command lines the way a POSIX shell quotes them, without the rest of a shell.</summary>
public static class ShellWords
{
    public static List<string> Split(string commandLine)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var inArgument = false;
        char? quote = null;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (quote != null)
            {
                if (c == quote)
                    quote = null;
                else if (c == '\\' && quote == '"' && i + 1 < commandLine.Length && commandLine[i + 1] is '"' or '\\')
                    current.Append(commandLine[++i]);
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inArgument = true;
            }
            else if (c == '\\' && i + 1 < commandLine.Length)
            {
                current.Append(commandLine[++i]);
                inArgument = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inArgument)
                    arguments.Add(current.ToString());

                current.Clear();
                inArgument = false;
            }
            else
            {
                current.Append(c);
                inArgument = true;
            }
        }

        if (quote != null)
            throw new ArgumentException("The command has a quote that isn't closed.");

        if (inArgument)
            arguments.Add(current.ToString());

        return arguments;
    }

    public static string Join(IEnumerable<string>? arguments) =>
        string.Join(' ', (arguments ?? []).Select(Quote));

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=+,@%".Contains(c))
            ? argument
            : "'" + argument.Replace("'", "'\\''") + "'";
}
