using System.Reflection;
using System.Runtime.Loader;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.Docker.Tests;

/// <summary>
/// Loads the built drop-in folder the way the server does, to prove it carries what it needs and
/// nothing that would load a second copy of a host type.
/// </summary>
public sealed class PluginLoadingTests : IDisposable
{
    private const string PluginAssembly = "LANCommander.Plugins.Docker";

    private readonly ScratchDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TheDropInFolderHoldsThePluginAndTheDockerClient()
    {
        var assemblies = System.IO.Directory.GetFiles(PluginOutputDirectory(), "*.dll").Select(Path.GetFileNameWithoutExtension).Order();

        Assert.Equal(["Docker.DotNet", PluginAssembly], assemblies);
        Assert.Empty(System.IO.Directory.GetDirectories(PluginOutputDirectory()));
    }

    [Fact]
    public void TheServerLoadsThePluginAndFindsItsEngine()
    {
        var services = new ServiceCollection();
        var loader = new PluginLoaderService();

        loader.DiscoverAndConfigure(services, PluginHost.Server, Deploy(), "2.2.0", NullLogger.Instance);

        Assert.Equal(DockerPlugin.PluginId, Assert.Single(loader.LoadedPlugins).Id);

        // Registered as the host's interface types, so the server finds them
        Assert.Contains(services, service => service.ServiceType == typeof(IServerEngineExtension));

        var navigation = services
            .Where(service => service.ServiceType == typeof(IServerNavigationExtension))
            .Select(service => (IServerNavigationExtension)Activator.CreateInstance(service.ImplementationType!, nonPublic: true)!)
            .Single();

        Assert.Equal(DockerRoutes.Hosts, navigation.Href);
        Assert.Equal(ServerNavigationSection.Settings, navigation.Section);
        Assert.Equal("/Settings/Servers", navigation.After);

        var routes = (IServerRouteAssemblyExtension)services
            .Single(service => service.ServiceType == typeof(IServerRouteAssemblyExtension)).ImplementationInstance!;

        var pluginContext = AssemblyLoadContext.GetLoadContext(routes.Assembly);

        Assert.IsType<PluginLoadContext>(pluginContext);

        // Every LANCommander assembly the plugin references resolves to the host's copy
        foreach (var reference in routes.Assembly.GetReferencedAssemblies().Where(reference => reference.Name!.StartsWith("LANCommander.", StringComparison.Ordinal)))
        {
            var loaded = pluginContext!.LoadFromAssemblyName(reference);

            Assert.True(
                AssemblyLoadContext.GetLoadContext(loaded) == AssemblyLoadContext.Default,
                $"{reference.Name} loaded into {AssemblyLoadContext.GetLoadContext(loaded)?.Name}");
        }

        // and the Docker client is the plugin's own
        Assert.Same(pluginContext, AssemblyLoadContext.GetLoadContext(pluginContext!.LoadFromAssemblyName(new AssemblyName("Docker.DotNet"))));
    }

    [Theory]
    [InlineData("2.2.0-alpha.4", false)]
    [InlineData("2.2.0-beta.1", true)]
    [InlineData("2.2.0", true)]
    public void TheServerSkipsThePluginWhenItPredatesServerEngines(string hostVersion, bool loads)
    {
        var loader = new PluginLoaderService();

        loader.DiscoverAndConfigure(new ServiceCollection(), PluginHost.Server, Deploy(), hostVersion, NullLogger.Instance);

        Assert.Equal(loads, loader.LoadedPlugins.Count == 1);
    }

    private string Deploy()
    {
        var root = Path.Combine(_directory.Path, "Plugins");
        var folder = Path.Combine(root, PluginAssembly);

        System.IO.Directory.CreateDirectory(folder);

        foreach (var file in System.IO.Directory.GetFiles(PluginOutputDirectory()))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));

        return root;
    }

    private static string PluginOutputDirectory()
        => typeof(PluginLoadingTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PluginOutputDirectory").Value!;
}
