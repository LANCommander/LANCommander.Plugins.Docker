using LANCommander.Plugins.Docker.Containers;

namespace LANCommander.Plugins.Docker.Tests;

public sealed class PortConflictTests
{
    private static readonly Dictionary<string, string> Drivers = new()
    {
        ["bridge"] = "bridge",
        ["host"] = "host",
        ["games"] = "bridge",
        ["lan"] = "macvlan",
        ["lan2"] = "ipvlan",
    };

    private static PortMapping Port(int container, int? host, string protocol = "udp", string? hostIp = null) =>
        new() { ContainerPort = container, HostPort = host, Protocol = protocol, HostIp = hostIp };

    [Theory]
    [InlineData("", NetworkKind.Bridge)]
    [InlineData("default", NetworkKind.Bridge)]
    [InlineData("bridge", NetworkKind.Bridge)]
    [InlineData("games", NetworkKind.Bridge)]
    [InlineData("host", NetworkKind.Host)]
    [InlineData("lan", NetworkKind.OwnAddress)]
    [InlineData("lan2", NetworkKind.OwnAddress)]
    [InlineData("none", NetworkKind.None)]
    [InlineData("container:abc", NetworkKind.None)]
    [InlineData("unknown", NetworkKind.Bridge)]
    public void NetworksAreKnownByTheirDrivers(string network, NetworkKind kind)
    {
        Assert.Equal(kind, PortConflicts.KindOf(network, Drivers));
    }

    [Fact]
    public void APublishedPortAnotherContainerPublishesConflicts()
    {
        var others = new[] { new PortUse("quake3", true, "udp", "", 27960), new PortUse("rtcw", false, "udp", "", 27960) };

        var conflict = Assert.Single(PortConflicts.Find([Port(27960, 27960), Port(80, 8080, "tcp")], NetworkKind.Bridge, others));

        Assert.Equal(27960, conflict.Port.ContainerPort);
        Assert.Contains("quake3, rtcw (stopped)", conflict.Message);
    }

    [Fact]
    public void TheSamePortNumberOverAnotherProtocolDoesNotConflict()
    {
        Assert.Empty(PortConflicts.Find([Port(27015, 27015, "udp")], NetworkKind.Bridge, [new PortUse("srcds", true, "tcp", "", 27015)]));
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.2", false)]
    [InlineData("10.0.0.1", "10.0.0.1", true)]
    [InlineData("10.0.0.1", "", true)]
    [InlineData("", "0.0.0.0", true)]
    public void PortsOnDifferentHostAddressesDoNotConflict(string mine, string theirs, bool conflicts)
    {
        var found = PortConflicts.Find([Port(7777, 7777, hostIp: mine)], NetworkKind.Bridge, [new PortUse("other", true, "udp", theirs, 7777)]);

        Assert.Equal(conflicts, found.Count > 0);
    }

    [Fact]
    public void UnpublishedPortsDoNotConflictBehindTheBridge()
    {
        Assert.Empty(PortConflicts.Find([Port(27960, null)], NetworkKind.Bridge, [new PortUse("other", true, "udp", "", 27960)]));
    }

    [Fact]
    public void WithHostNetworkingTheContainersOwnPortsAreTheHosts()
    {
        // The host port published is meaningless; the server listens on 27960 itself
        var conflict = Assert.Single(PortConflicts.Find([Port(27960, 30000)], NetworkKind.Host, [new PortUse("quake3", true, "udp", "", 27960)]));

        Assert.Contains("27960/udp", conflict.Message);
    }

    [Fact]
    public void AContainerWithAnAddressOfItsOwnNeverConflicts()
    {
        Assert.Empty(PortConflicts.Find([Port(27960, 27960)], NetworkKind.OwnAddress, [new PortUse("quake3", true, "udp", "", 27960)]));
        Assert.Empty(PortConflicts.UsesOf("lan-server", true, [Port(27960, 27960)], NetworkKind.OwnAddress));
    }

    [Fact]
    public void HostNetworkedContainersTakeTheirOwnPorts()
    {
        var uses = PortConflicts.UsesOf("bf1942", false, [Port(14567, null), Port(4711, null, "tcp")], NetworkKind.Host).ToList();

        Assert.Equal(["14567/udp", "4711/tcp"], uses.Select(u => $"{u.Port}/{u.Protocol}"));
        Assert.All(uses, u => Assert.False(u.Running));
    }

    [Fact]
    public void AHostPortGivenTwiceConflicts()
    {
        var conflicts = PortConflicts.Find([Port(7777, 7777), Port(7778, 7777)], NetworkKind.Bridge, []);

        Assert.Equal(2, conflicts.Count);
        Assert.All(conflicts, c => Assert.Contains("more than one", c.Message));
    }

    [Fact]
    public void PortsAreMovedTogetherPastTheTakenOnes()
    {
        // A game port and its query port, one apart, as games expect them
        var ports = new List<PortMapping> { Port(7777, 7777), Port(7778, 7778) };
        var others = new[] { new PortUse("a", true, "udp", "", 7777), new PortUse("b", true, "udp", "", 7779) };

        Assert.Equal(3, PortConflicts.FreeOffset(ports, NetworkKind.Bridge, others));
        Assert.Equal(0, PortConflicts.FreeOffset(ports, NetworkKind.Bridge, []));
        Assert.Null(PortConflicts.FreeOffset(ports, NetworkKind.Host, others));
    }

    [Fact]
    public void PortsCantBeMovedPastTheLastPort()
    {
        Assert.Null(PortConflicts.FreeOffset([Port(65535, 65535)], NetworkKind.Bridge, [new PortUse("a", true, "udp", "", 65535)]));
    }
}
