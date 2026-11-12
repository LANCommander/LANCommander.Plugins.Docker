using LANCommander.Plugins.Docker.Containers;
using LANCommander.Plugins.Docker.Engine;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: LANCommanderPlugin(
    typeof(LANCommander.Plugins.Docker.DockerPlugin),
    Id = LANCommander.Plugins.Docker.DockerPlugin.PluginId,
    MinHostVersion = "2.2.0-beta.4",
    Hosts = PluginHost.Server)]

namespace LANCommander.Plugins.Docker;

/// <summary>
/// Runs game servers as Docker containers. Adds a Docker engine to a server's General page, a page
/// for each server's container, and the daemons it connects to under the engine on Settings › Servers.
/// </summary>
public sealed class DockerPlugin : IPlugin
{
    public const string PluginId = "lancommander.docker";

    public string Id => PluginId;
    public string Name => "Docker";
    public string Version => typeof(DockerPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string Author => "LANCommander";

    public void ConfigureServices(IServiceCollection services)
    {
        AddDockerServices(services);

        services.AddSingleton<IServerEngineExtension>(provider => provider.GetRequiredService<DockerServerEngine>());
    }

    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        var hosts = context.Services.GetRequiredService<DockerHosts>().All;

        context.Logger.LogInformation("Docker plugin connects to {Count} Docker host(s)", hosts.Count);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers everything the plugin needs that doesn't come from the host, so the tests can build
    /// the same container.
    /// </summary>
    internal static void AddDockerServices(IServiceCollection services)
    {
        services.AddSingleton<DockerHosts>();
        services.AddSingleton<ContainerService>();
        services.AddSingleton<Images.ImageCatalog>();
        services.AddSingleton<DockerServerEngine>();
    }
}

/// <summary>The server's pages the plugin links to.</summary>
public static class DockerRoutes
{
    /// <summary>Settings › Servers, where the Docker engine's hosts are configured.</summary>
    public const string Hosts = "/Settings/Servers";
}
