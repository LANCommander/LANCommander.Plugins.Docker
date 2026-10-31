using Bunit;
using Docker.DotNet.Models;
using LANCommander.Plugins.Docker.Components;
using LANCommander.Plugins.Docker.Containers;
using LANCommander.Plugins.Docker.Engine;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.Plugins.Docker.Pages;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using LANCommander.Server.UI.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace LANCommander.Plugins.Docker.Tests;

public sealed class PageTests : BunitContext
{
    private readonly MemorySettings<DockerSettings> _settings = new();
    private readonly DockerHost _local = new() { Name = "Local", Address = DockerHost.DefaultAddress };
    private readonly FakeServers _servers = new();
    private readonly string _name = $"lancommander-test-{Guid.NewGuid():N}";

    // The server on the General page, as the server's own editor would keep it
    private ServerEngineServer _server = new() { Name = "Arena Host", Host = "10.0.0.1", Port = 0 };

    public PageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddLANCommanderServerUI();
        Services.AddLogging();
        Services.AddSingleton<IPluginSettings<DockerSettings>>(_settings);
        Services.AddSingleton<IServerProvider>(_servers);

        DockerPlugin.AddDockerServices(Services);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var hosts = Services.GetRequiredService<DockerHosts>();

            if (hosts.Find(_local.Id) != null)
            {
                var client = hosts.GetClient(_local.Id);

                foreach (var container in client.Containers.ListContainersAsync(new ContainersListParameters { All = true }).GetAwaiter().GetResult())
                {
                    if (container.Names.Any(name => name.TrimStart('/').StartsWith(_name, StringComparison.Ordinal)))
                        client.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true }).GetAwaiter().GetResult();
                }
            }
        }

        base.Dispose(disposing);
    }

    private ServerEngineEditorContext Context() => new(() => _server, change => _server = change(_server));

    [Fact]
    public void WithoutHostsTheEditorSaysWhereToAddOne()
    {
        var editor = Render<DockerServerEditor>(parameters => parameters.Add(p => p.Context, Context()));

        Assert.Contains("No Docker hosts yet", editor.Markup);
        Assert.Contains($"href=\"{DockerRoutes.Hosts}\"", editor.Markup);
    }

    [Fact]
    public async Task AnAdministratorAddsAHost()
    {
        var page = Render<HostsPage>();

        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Add Host").ClickAsync(new());

        // The first host is offered as this machine's daemon
        Assert.Equal(DockerHost.DefaultAddress, page.FindAll("input").Single(i => i.GetAttribute("placeholder") == DockerHost.DefaultAddress).GetAttribute("value"));

        await page.Find("form").SubmitAsync();

        var host = Assert.Single(_settings.Current.Hosts);

        Assert.Equal("Local", host.Name);
        Assert.Equal(DockerHost.DefaultAddress, host.Address);
    }

    [DockerFact]
    public async Task ChoosingAContainerKeepsItWithTheServerAndOffersItsPorts()
    {
        _settings.Current.Hosts.Add(_local);

        await Services.GetRequiredService<ContainerService>().CreateAsync(_local.Id, new ContainerDefinition
        {
            Name = _name,
            Image = DockerEngineTests.Image,
            Command = "sleep 600",
            Ports = [new PortMapping { ContainerPort = 27015, HostPort = 47015 }],
        });

        _server = _server with { Settings = ServerEngineSettings.Write(new DockerServerSettings { HostId = _local.Id }) };

        var editor = Render<DockerServerEditor>(parameters => parameters.Add(p => p.Context, Context()));

        editor.WaitForAssertion(() => Assert.Contains(_name, editor.Markup));

        await editor.InvokeAsync(() => editor.FindComponents<LANCommander.Server.UI.Controls.Select<string>>()
            .Single().Instance.ValueChanged.InvokeAsync(_name));

        Assert.Equal(_name, _server.GetSettings<DockerServerSettings>().ContainerId);
        Assert.Equal(_local.Id, _server.GetSettings<DockerServerSettings>().HostId);

        editor.WaitForAssertion(() => Assert.Contains("47015:27015/udp", editor.Markup));

        await editor.FindAll("button").Single(b => b.TextContent.Contains("47015:27015/udp")).ClickAsync(new());

        Assert.Equal(47015, _server.Port);
        Assert.Equal("10.0.0.1", _server.Host);
    }

    [DockerFact]
    public async Task TheContainerPageShowsTheContainerAsItIs()
    {
        _settings.Current.Hosts.Add(_local);

        await Services.GetRequiredService<ContainerService>().CreateAsync(_local.Id, new ContainerDefinition
        {
            Name = _name,
            Image = DockerEngineTests.Image,
            Command = "sleep 600",
            Environment = [new EnvironmentVariable { Name = "MAX_PLAYERS", Value = "16" }],
        });

        var server = new ServerEngineServer
        {
            Id = Guid.NewGuid(),
            Settings = ServerEngineSettings.Write(new DockerServerSettings { HostId = _local.Id, ContainerId = _name }),
        };

        var page = Render<ContainerSection>(parameters => parameters.Add(p => p.Server, server));

        page.WaitForAssertion(() => Assert.Contains("Apply", page.Markup));

        var values = page.FindAll("input").Select(i => i.GetAttribute("value")).ToList();

        Assert.Contains(_name, values);
        Assert.Contains(DockerEngineTests.Image, values);
        Assert.Contains("sleep 600", values);
        Assert.Contains("MAX_PLAYERS", values);
        Assert.Contains("16", values);
    }

    [Fact]
    public void TheContainerPageAsksForAContainerFirst()
    {
        var page = Render<ContainerSection>(parameters => parameters.Add(p => p.Server, new ServerEngineServer { Id = Guid.NewGuid() }));

        page.WaitForAssertion(() => Assert.Contains("Choose a Docker host and container", page.Markup));
    }

    /// <summary>The server's records, for the Container page to point a server at a replaced container.</summary>
    private sealed class FakeServers : IServerProvider
    {
        public Task<IReadOnlyList<ServerInfo>> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ServerInfo>>([]);
        public Task<ServerInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ServerInfo?>(null);
        public Task<ServerInfo> AddAsync(ServerInfo server, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServerInfo> UpdateAsync(ServerInfo server, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ServerConsoleInfo>> GetConsolesAsync(Guid serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServerConsoleInfo> AddConsoleAsync(Guid serverId, ServerConsoleInfo console, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServerConsoleInfo> UpdateConsoleAsync(ServerConsoleInfo console, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteConsoleAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ServerHttpPathInfo>> GetHttpPathsAsync(Guid serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServerHttpPathInfo> AddHttpPathAsync(Guid serverId, ServerHttpPathInfo httpPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServerHttpPathInfo> UpdateHttpPathAsync(ServerHttpPathInfo httpPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteHttpPathAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
