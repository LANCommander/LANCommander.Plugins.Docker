using LANCommander.Plugins.Docker.Hosts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.Docker.Tests;

public sealed class DockerHostsTests
{
    private static readonly Guid LegacyId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static IConfiguration ServerSettings() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:GameServers:ServerEngines:0:Id"] = Guid.NewGuid().ToString(),
            ["Server:GameServers:ServerEngines:0:Name"] = "Local",
            ["Server:GameServers:ServerEngines:0:Type"] = "Local",
            ["Server:GameServers:ServerEngines:1:Id"] = LegacyId.ToString(),
            ["Server:GameServers:ServerEngines:1:Name"] = "Rack",
            ["Server:GameServers:ServerEngines:1:Type"] = "Docker",
            ["Server:GameServers:ServerEngines:1:Address"] = "tcp://rack:2375",
            ["Server:GameServers:ServerEngines:2:Id"] = Guid.NewGuid().ToString(),
            ["Server:GameServers:ServerEngines:2:Name"] = "Other LANCommander",
            ["Server:GameServers:ServerEngines:2:Type"] = "Remote",
            ["Server:GameServers:ServerEngines:2:Address"] = "http://other:1337",
        })
        .Build();

    [Fact]
    public void TheBuiltInEnginesDockerHostsAreTakenOverWithTheirIds()
    {
        var settings = new MemorySettings<DockerSettings>();
        var hosts = new DockerHosts(settings, NullLogger<DockerHosts>.Instance);

        hosts.ImportLegacyHosts(ServerSettings());

        var host = Assert.Single(hosts.All);

        Assert.Equal(LegacyId, host.Id);
        Assert.Equal("Rack", host.Name);
        Assert.Equal("tcp://rack:2375", host.Address);
        Assert.True(settings.Current.LegacyHostsImported);
    }

    [Fact]
    public void AHostRemovedAfterBeingTakenOverStaysRemoved()
    {
        var settings = new MemorySettings<DockerSettings>();
        var hosts = new DockerHosts(settings, NullLogger<DockerHosts>.Instance);

        hosts.ImportLegacyHosts(ServerSettings());
        hosts.Remove(LegacyId);
        hosts.ImportLegacyHosts(ServerSettings());

        Assert.Empty(hosts.All);
    }

    [Fact]
    public void AnAddressThatIsNotOneIsRefused()
    {
        var hosts = TestHosts.Create();

        Assert.Throws<DockerHostException>(() => hosts.Save(new DockerHost { Name = "Bad", Address = "not an address" }));
        Assert.Throws<ArgumentException>(() => hosts.Save(new DockerHost { Name = " ", Address = DockerHost.DefaultAddress }));
        Assert.Empty(hosts.All);
    }

    [Fact]
    public void AServerWithoutAHostIsToldWhy()
    {
        var hosts = TestHosts.Create();

        Assert.Contains("No Docker host", Assert.Throws<DockerHostException>(() => hosts.GetClient((Guid?)null)).Message);
        Assert.Contains("removed", Assert.Throws<DockerHostException>(() => hosts.GetClient(Guid.NewGuid())).Message);
    }

    [Fact]
    public void AHostsClientIsReplacedWhenItsAddressChanges()
    {
        var host = new DockerHost { Name = "Local", Address = "tcp://one:2375" };
        var hosts = TestHosts.Create(host);

        var first = hosts.GetClient(host.Id);

        Assert.Same(first, hosts.GetClient(host.Id));

        hosts.Save(new DockerHost { Id = host.Id, Name = "Local", Address = "tcp://two:2375" });

        Assert.NotSame(first, hosts.GetClient(host.Id));
    }

    [Fact]
    public void HostsAreReplacedTogetherOrNotAtAll()
    {
        var kept = new DockerHost { Name = "Kept", Address = "tcp://kept:2375" };
        var removed = new DockerHost { Name = "Removed", Address = "tcp://removed:2375" };
        var hosts = TestHosts.Create(kept, removed);

        // One bad host stops all of them being saved
        Assert.Throws<DockerHostException>(() => hosts.Replace([kept, new DockerHost { Name = "Bad", Address = "not an address" }]));
        Assert.Equal(["Kept", "Removed"], hosts.All.Select(h => h.Name));

        hosts.Replace([new DockerHost { Id = kept.Id, Name = " Kept ", Address = "tcp://kept:2376" }, new DockerHost { Name = "Added", Address = "tcp://added:2375" }]);

        Assert.Equal(["Added", "Kept"], hosts.All.Select(h => h.Name));
        Assert.Equal("tcp://kept:2376", hosts.Find(kept.Id)!.Address);
        Assert.Null(hosts.Find(removed.Id));
    }
}
