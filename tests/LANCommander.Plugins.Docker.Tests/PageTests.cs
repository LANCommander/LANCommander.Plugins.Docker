using Bunit;
using Docker.DotNet.Models;
using LANCommander.Plugins.Docker.Components;
using LANCommander.Plugins.Docker.Containers;
using LANCommander.Plugins.Docker.Engine;
using LANCommander.Plugins.Docker.Hosts;
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

    // What Settings › Servers runs when it's saved
    private readonly List<Func<CancellationToken, Task>> _onSave = [];

    private IRenderedComponent<DockerEngineSettings> RenderSettings() => Render<DockerEngineSettings>(parameters => parameters
        .Add(p => p.Context, new ServerEngineSettingsContext(save =>
        {
            _onSave.Add(save);

            return new Unregister(() => _onSave.Remove(save));
        })));

    private async Task SaveSettingsPageAsync(IRenderedComponent<DockerEngineSettings> settings)
    {
        foreach (var save in _onSave.ToList())
            await settings.InvokeAsync(() => save(CancellationToken.None));
    }

    private sealed class Unregister(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }

    [Fact]
    public void WithoutHostsTheEditorSaysWhereToAddOne()
    {
        var editor = Render<DockerServerEditor>(parameters => parameters.Add(p => p.Context, Context()));

        Assert.Contains("No Docker hosts yet", editor.Markup);
        Assert.Contains($"href=\"{DockerRoutes.Hosts}\"", editor.Markup);
    }

    [Fact]
    public async Task AnAdministratorAddsAHostAndItsSavedWithThePage()
    {
        var page = RenderSettings();

        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Add Host").ClickAsync(new());

        // The first host is offered as this machine's daemon
        Assert.Equal(DockerHost.DefaultAddress, page.FindAll("input").Single(i => i.GetAttribute("placeholder") == DockerHost.DefaultAddress).GetAttribute("value"));

        await page.Find("form").SubmitAsync();

        // Listed, but not saved until the page is
        Assert.Contains("Not saved", page.Markup);
        Assert.Empty(_settings.Current.Hosts);

        await SaveSettingsPageAsync(page);

        var host = Assert.Single(_settings.Current.Hosts);

        Assert.Equal("Local", host.Name);
        Assert.Equal(DockerHost.DefaultAddress, host.Address);
        Assert.DoesNotContain("Not saved", page.Markup);
    }

    [Fact]
    public async Task AWrongCatalogUrlIsntSavedAndNeitherAreTheHosts()
    {
        var remote = new DockerHost { Name = "Remote", Address = "tcp://127.0.0.1:1" };

        _settings.Current.Hosts.Add(remote);

        var page = RenderSettings();

        await page.InvokeAsync(() => page.FindComponents<LANCommander.Server.UI.Controls.TextInput>()
            .Single(i => i.Instance.Placeholder?.StartsWith("None") == true).Instance.ValueChanged.InvokeAsync("ftp://example.com/index.json"));

        // Removing the host, too
        await page.InvokeAsync(() => page.FindComponent<LANCommander.Server.UI.Controls.Popconfirm>().Instance.OnConfirm.InvokeAsync());

        Assert.DoesNotContain(remote.Address, page.Markup);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SaveSettingsPageAsync(page));

        Assert.Contains("isn't an http or https address", error.Message);
        Assert.Equal("", _settings.Current.ImageCatalogUrl);
        Assert.Single(_settings.Current.Hosts);
    }

    [Fact]
    public void ClosingThePanelStopsItBeingSaved()
    {
        var page = RenderSettings();

        Assert.Single(_onSave);

        page.Instance.Dispose();

        Assert.Empty(_onSave);
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

        form.WaitForAssertion(() => Assert.Contains("Couldn't check for collisions", form.Markup));

        // The image's variables are offered as names to pick, not listed
        Assert.Contains("Pick a variable the image sets", form.Markup);
        Assert.DoesNotContain("DX_PORT=7790", form.Markup);
        Assert.Contains("used directly on the host", form.Markup);
        Assert.Contains("shared by every server of this game on the host", form.Markup);

        // An official image always gets the privileges its overlay needs
        Assert.True(spec.CanMountOverlay);
    }

    [DockerFact]
    public async Task LogFilesAreTheImagesAndThoseChosenFromTheContainersFiles()
    {
        _settings.Current.Hosts.Add(_local);

        var client = Services.GetRequiredService<DockerHosts>().GetClient(_local.Id);

        await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = _name,
            Image = DockerEngineTests.Image,
            Cmd = ["sh", "-c", "mkdir -p /srv/logs && echo started > /srv/logs/server.log && sleep 600"],
            Labels = new Dictionary<string, string> { [ContainerLogFile.LabelPrefix + "chat"] = "/srv/logs/chat.log" },
            HostConfig = new HostConfig { Binds = [$"{_name}-logs:/srv/logs"] },
        });
        await client.Containers.StartContainerAsync(_name, new ContainerStartParameters());

        _server = _server with
        {
            Settings = ServerEngineSettings.Write(new DockerServerSettings
            {
                HostId = _local.Id,
                ContainerId = _name,
                LogFiles = [new ContainerLogFile { Name = "Old", Path = "/srv/old.log" }],
            }),
        };

        var editor = Render<DockerServerEditor>(parameters => parameters.Add(p => p.Context, Context()));

        editor.WaitForAssertion(() => Assert.Contains("From the image", editor.Markup));
        Assert.Contains("/srv/logs/chat.log", editor.Markup);

        // The volume is only reached through the container, from the folder it's mounted at
        Assert.Contains("Only while running", editor.Markup);

        await editor.FindAll("button").First(b => b.GetAttribute("title") == "Remove").ClickAsync(new());
        await editor.FindAll("button").Single(b => b.TextContent.Trim() == "Add Log File").ClickAsync(new());

        editor.WaitForAssertion(() => Assert.Contains("server.log", editor.FindAll("button").Select(b => b.TextContent.Trim())));

        await editor.FindAll("button").Single(b => b.TextContent.Trim() == "server.log").ClickAsync(new());

        var file = Assert.Single(_server.GetSettings<DockerServerSettings>().LogFiles);

        Assert.Equal("/srv/logs/server.log", file.Path);
        Assert.Equal("server.log", file.Name);
    }

    [DockerFact]
    public async Task AContainerOnTheHostsNetworkOffersThePortsItsImageDeclares()
    {
        _settings.Current.Hosts.Add(_local);

        var client = Services.GetRequiredService<DockerHosts>().GetClient(_local.Id);

        await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = _name,
            Image = DockerEngineTests.Image,
            Cmd = ["sleep", "600"],
            ExposedPorts = new Dictionary<string, EmptyStruct> { ["27960/udp"] = default },
            HostConfig = new HostConfig { NetworkMode = "host" },
        });

        _server = _server with { Settings = ServerEngineSettings.Write(new DockerServerSettings { HostId = _local.Id, ContainerId = _name }) };

        var editor = Render<DockerServerEditor>(parameters => parameters.Add(p => p.Context, Context()));

        editor.WaitForAssertion(() => Assert.Contains("host's own network", editor.Markup));

        await editor.FindAll("button").Single(b => b.TextContent.Trim() == "27960/udp").ClickAsync(new());

        Assert.Equal(27960, _server.Port);
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

        var page = RenderSettings();

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
    public void WithoutUnusedContainersTheEngineSettingsSaySo()
    {
        var page = RenderSettings();

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
        public Task<IReadOnlyList<ServerFileInfo>> ListFilesAsync(Guid serverId, string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> OpenFileAsync(Guid serverId, string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WriteFileAsync(Guid serverId, string relativePath, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
