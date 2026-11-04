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

    [DockerFact]
    public async Task ANewContainerIsWarnedOfPortsAnotherPublishes()
    {
        _settings.Current.Hosts.Add(_local);

        await Services.GetRequiredService<ContainerService>().CreateAsync(_local.Id, new ContainerDefinition
        {
            Name = _name,
            Image = DockerEngineTests.Image,
            Command = "sleep 600",
            Ports = [new PortMapping { ContainerPort = 27960, HostPort = 47960 }],
        });

        var spec = new ContainerDefinition
        {
            Name = _name + "-second",
            Image = DockerEngineTests.Image,
            Ports = [new PortMapping { ContainerPort = 27960, HostPort = 47960 }],
        };

        var form = Render<ContainerForm>(parameters => parameters
            .Add(p => p.Spec, spec)
            .Add(p => p.HostId, _local.Id));

        form.WaitForAssertion(() => Assert.Contains($"also taken by {_name} (stopped)", form.Markup));

        await form.FindAll("button").Single(b => b.TextContent.Contains("Move host ports up by 1")).ClickAsync(new());

        Assert.Equal(47961, spec.Ports.Single().HostPort);
        form.WaitForAssertion(() => Assert.DoesNotContain("may collide", form.Markup));
    }

    [Fact]
    public void TheFormShowsWhatTheImageGivesAndWhyCollisionsArentChecked()
    {
        var spec = new ContainerDefinition
        {
            Name = "deusex",
            Image = "lancommander/deusex",
            Network = "host",
            Ports = [new PortMapping { ContainerPort = 7790, HostPort = 7790 }],
            Mounts = [new VolumeMount { Source = "/data/Servers/deusex/Base", Destination = "/config/Server" }],
            Defaults = new ImageDefaults { Environment = [new EnvironmentVariable { Name = "DX_PORT", Value = "7790" }] },
        };

        // A host that can't be reached
        var form = Render<ContainerForm>(parameters => parameters
            .Add(p => p.Spec, spec)
            .Add(p => p.HostId, Guid.NewGuid()));

        form.WaitForAssertion(() => Assert.Contains("collisions and shared folders aren't checked", form.Markup));

        Assert.Contains("DX_PORT=7790", form.Markup);
        Assert.Contains("host ports don't apply", form.Markup);
        Assert.Contains("for every server of this game on the host to share", form.Markup);
    }

    [Fact]
    public async Task LogFilesAreKeptWithTheServer()
    {
        _server = _server with
        {
            Settings = ServerEngineSettings.Write(new DockerServerSettings
            {
                HostId = Guid.NewGuid(),
                ContainerId = "deusex",
                LogFiles = [new ContainerLogFile { Name = "Server log", Path = "/config/Overlay/System/server.log" }],
            }),
        };

        var editor = Render<DockerServerEditor>(parameters => parameters.Add(p => p.Context, Context()));

        Assert.Contains("/config/Overlay/System/server.log", editor.FindAll("input").Select(i => i.GetAttribute("value")));

        await editor.FindAll("button").Single(b => b.TextContent.Trim() == "Add Log File").ClickAsync(new());
        await editor.FindAll("button").First(b => b.GetAttribute("title") == "Remove").ClickAsync(new());

        // The new one is left, empty
        var file = Assert.Single(_server.GetSettings<DockerServerSettings>().LogFiles);

        Assert.Equal("", file.Path);
    }

    [Fact]
    public async Task AnImageIsPickedWithTheServersGameFirst()
    {
        // Docker Hub as it lists LANCommander's images; no GitHub, and a host that can't be reached
        Services.AddSingleton(new LANCommander.Plugins.Docker.Images.ImageCatalog(
            TestHosts.Create(),
            new MemorySettings<DockerSettings>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LANCommander.Plugins.Docker.Images.ImageCatalog>.Instance,
            new HttpClient(new HubOnly("""
                { "next": null, "results": [
                  { "name": "quake3", "description": "" },
                  { "name": "deusex", "description": "" },
                  { "name": "wine", "description": "" }
                ] }
                """))));

        string? picked = null;

        var picker = Render<ImagePicker>(parameters => parameters
            .Add(p => p.HostId, Guid.NewGuid())
            .Add(p => p.GameTitle, "Deus Ex")
            .Add(p => p.OnPick, (string image) => picked = image));

        picker.WaitForAssertion(() => Assert.Contains("lancommander/deusex", picker.Markup));

        // The game's own first, then the others, then the base images
        var markup = picker.Markup;

        Assert.True(markup.IndexOf("lancommander/deusex", StringComparison.Ordinal) < markup.IndexOf("lancommander/quake3", StringComparison.Ordinal));
        Assert.True(markup.IndexOf("lancommander/quake3", StringComparison.Ordinal) < markup.IndexOf("lancommander/wine", StringComparison.Ordinal));
        Assert.Contains("For Deus Ex", markup);

        // Searching narrows it down
        await picker.InvokeAsync(() => picker.FindComponents<LANCommander.Server.UI.Controls.TextInput>().First().Instance.ValueChanged.InvokeAsync("quake"));

        picker.WaitForAssertion(() => Assert.DoesNotContain("lancommander/deusex", picker.Markup));

        await picker.FindAll("button").Single(b => b.TextContent.Trim() == "Use").ClickAsync(new());

        picker.WaitForAssertion(() => Assert.Equal("lancommander/quake3:latest", picked));
    }

    /// <summary>Docker Hub's answer to every request for it; anything else can't be reached.</summary>
    private sealed class HubOnly(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.Host == "hub.docker.com" && !request.RequestUri.AbsolutePath.Contains("/tags")
                ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) })
                : throw new HttpRequestException("No such host is known.");
    }

    [DockerFact]
    public async Task AContainerNoServerUsesIsListedAndRemoved()
    {
        _settings.Current.Hosts.Add(_local);

        await Services.GetRequiredService<ContainerService>().CreateAsync(_local.Id, new ContainerDefinition { Name = _name, Image = DockerEngineTests.Image, Command = "sleep 600" });

        var page = Render<HostsPage>();

        page.WaitForAssertion(() => Assert.Contains(_name, page.Markup));

        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Remove" && b.ParentElement!.TextContent.Contains(_name)).ClickAsync(new());

        // The removal card, then its confirmation
        page.WaitForAssertion(() => Assert.Contains($"Remove {_name}", page.Markup));

        var card = page.FindComponent<RemoveContainerCard>();

        // Once it's read what the container has
        card.WaitForState(() => card.FindComponents<LANCommander.Server.UI.Controls.Popconfirm>().Count > 0);

        await page.InvokeAsync(() => card.FindComponent<LANCommander.Server.UI.Controls.Popconfirm>().Instance.OnConfirm.InvokeAsync());

        page.WaitForAssertion(() => Assert.DoesNotContain(_name, page.Markup));

        var containers = await Services.GetRequiredService<ContainerService>().ListAsync(_local.Id);

        Assert.DoesNotContain(containers, c => c.Name == _name);
    }

    [Fact]
    public void WithoutUnusedContainersTheHostsPageSaysSo()
    {
        var page = Render<HostsPage>();

        page.WaitForAssertion(() => Assert.Contains("Containers no server uses", page.Markup));
        Assert.Contains("None.", page.Markup);
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
