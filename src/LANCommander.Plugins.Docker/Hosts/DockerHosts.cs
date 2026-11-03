using System.Collections.Concurrent;
using Docker.DotNet;
using LANCommander.SDK.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Docker.Hosts;

/// <summary>
/// The Docker hosts the plugin connects to, and a client for each. Clients are made on first use and
/// replaced when a host's address changes.
/// </summary>
public sealed class DockerHosts(IPluginSettings<DockerSettings> settings, ILogger<DockerHosts> logger) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, (string Address, DockerClient Client)> _clients = new();

    /// <summary>The hosts, ordered by name.</summary>
    public IReadOnlyList<DockerHost> All => settings.Current.Hosts
        .OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public DockerHost? Find(Guid? id) => id == null ? null : settings.Current.Hosts.FirstOrDefault(h => h.Id == id);

    /// <summary>A client for the host.</summary>
    /// <exception cref="DockerHostException">There is no such host, or its address isn't one.</exception>
    public DockerClient GetClient(Guid? hostId)
    {
        var host = Find(hostId) ?? throw new DockerHostException(hostId == null
            ? "No Docker host has been chosen for this server."
            : "The server's Docker host has been removed. Choose another on its General page.");

        return GetClient(host);
    }

    /// <exception cref="DockerHostException">The host's address isn't one.</exception>
    public DockerClient GetClient(DockerHost host)
    {
        if (_clients.TryGetValue(host.Id, out var cached) && cached.Address == host.Address)
            return cached.Client;

        var client = CreateClient(host.Address);

        _clients.AddOrUpdate(host.Id, (host.Address, client), (_, old) =>
        {
            old.Client.Dispose();

            return (host.Address, client);
        });

        return client;
    }

    /// <summary>Asks a host for its version, to show that it can be reached.</summary>
    public async Task<string> TestAsync(DockerHost host, CancellationToken cancellationToken)
    {
        using var client = CreateClient(host.Address);

        var version = await client.System.GetVersionAsync(cancellationToken);

        return $"Docker {version.Version} ({version.Os}/{version.Arch})";
    }

    public void Save(DockerHost host)
    {
        if (string.IsNullOrWhiteSpace(host.Name))
            throw new ArgumentException("A Docker host needs a name.", nameof(host));

        CreateClient(host.Address).Dispose();

        settings.Update(s =>
        {
            s.Hosts.RemoveAll(h => h.Id == host.Id);
            s.Hosts.Add(new DockerHost { Id = host.Id, Name = host.Name.Trim(), Address = host.Address.Trim(), DataFolder = host.DataFolder?.Trim() ?? "" });
        });
    }

    public void Remove(Guid id)
    {
        settings.Update(s => s.Hosts.RemoveAll(h => h.Id == id));

        if (_clients.TryRemove(id, out var removed))
            removed.Client.Dispose();
    }

    /// <summary>
    /// Takes over the Docker hosts the server's built-in Docker engine used, once, keeping their ids
    /// so the servers that ran on them still find them.
    /// </summary>
    public void ImportLegacyHosts(IConfiguration configuration)
    {
        if (settings.Current.LegacyHostsImported)
            return;

        var legacy = configuration.GetSection("Server:GameServers:ServerEngines").GetChildren()
            .Where(engine => string.Equals(engine["Type"], "Docker", StringComparison.OrdinalIgnoreCase) || engine["Type"] == "1")
            .Select(engine => new DockerHost
            {
                Id = Guid.TryParse(engine["Id"], out var id) ? id : Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(engine["Name"]) ? "Docker" : engine["Name"]!,
                Address = engine["Address"] ?? "",
            })
            .Where(host => !string.IsNullOrWhiteSpace(host.Address))
            .ToList();

        settings.Update(s =>
        {
            foreach (var host in legacy.Where(host => s.Hosts.All(h => h.Id != host.Id)))
                s.Hosts.Add(host);

            s.LegacyHostsImported = true;
        });

        if (legacy.Count > 0)
            logger.LogInformation("Took over {Count} Docker host(s) from the server's settings: {Hosts}", legacy.Count, string.Join(", ", legacy.Select(h => h.Name)));
    }

    private static DockerClient CreateClient(string address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri))
            throw new DockerHostException($"\"{address}\" isn't a Docker address, such as {DockerHost.DefaultAddress} or tcp://docker-host:2375.");

        return new DockerClientConfiguration(uri).CreateClient();
    }

    public void Dispose()
    {
        foreach (var (_, client) in _clients.Values)
            client.Dispose();

        _clients.Clear();
    }
}

/// <summary>A Docker host can't be used, with a message for the administrator.</summary>
public sealed class DockerHostException(string message) : Exception(message);
