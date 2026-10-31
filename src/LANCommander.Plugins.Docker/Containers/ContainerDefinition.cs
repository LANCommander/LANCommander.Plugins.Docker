using System.Text;
using Docker.DotNet.Models;

namespace LANCommander.Plugins.Docker.Containers;

/// <summary>
/// What an administrator can change about a game server's container. Read from the container
/// itself, so there is nothing to keep in step with it; applying a change recreates the container,
/// since Docker can't change these on one that exists.
/// </summary>
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

    public static readonly IReadOnlyList<string> RestartPolicies = ["no", "on-failure", "unless-stopped", "always"];

    public static ContainerDefinition From(ContainerInspectResponse container)
    {
        var config = container.Config ?? new ContainerConfig();
        var host = container.HostConfig ?? new HostConfig();

        return new ContainerDefinition
        {
            Name = container.Name?.TrimStart('/') ?? "",
            Image = config.Image ?? "",
            Command = ShellWords.Join(config.Cmd),
            Environment = (config.Env ?? [])
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
        };
    }

    /// <summary>
    /// What to create the container from. With <paramref name="basis"/>, the container this one
    /// replaces, everything the spec doesn't cover is kept from it: entry point, user, network,
    /// limits and so on.
    /// </summary>
    public CreateContainerParameters ToCreateParameters(ContainerInspectResponse? basis = null)
    {
        Validate();

        var parameters = basis?.Config != null ? new CreateContainerParameters(basis.Config) : new CreateContainerParameters();
        var host = basis?.HostConfig ?? new HostConfig();

        // A hostname Docker made up from the old container's id would name the wrong container
        if (basis?.ID != null && parameters.Hostname != null && basis.ID.StartsWith(parameters.Hostname, StringComparison.Ordinal))
            parameters.Hostname = null;

        parameters.Name = Name.Trim();
        parameters.Image = Image.Trim();
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

        parameters.HostConfig = host;

        return parameters;
    }

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

        foreach (var variable in Environment.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
        {
            if (variable.Name.Contains('='))
                throw new ArgumentException($"The environment variable \"{variable.Name}\" can't have '=' in its name.");
        }
    }
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
