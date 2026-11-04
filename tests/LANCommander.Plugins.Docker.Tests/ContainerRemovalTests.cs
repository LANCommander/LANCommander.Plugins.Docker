using LANCommander.Plugins.Docker.Containers;

namespace LANCommander.Plugins.Docker.Tests;

public sealed class ContainerRemovalTests
{
    private const string Anonymous = "f00dfeedbeef0123456789abcdef0123456789abcdef0123456789abcdef0123";

    private static VolumeMount Mount(string source, string destination) => new() { Source = source, Destination = destination };

    private static readonly HostUsage Nobody = HostUsage.Empty;

    [Fact]
    public void AServersOwnFilesCanGoWithIt()
    {
        var plan = ContainerRemoval.Plan(
        [
            Mount("/data/Servers/quake3/quake3-ctf", "/config"),
            Mount("/data/Servers/quake3/Base", "/config/Server"),
            Mount("quake3-ctf-maps", "/maps"),
            Mount(Anonymous, "/data"),
        ], Nobody, "/data/Servers");

        Assert.All(plan, m => Assert.True(m.CanRemove));
        Assert.Equal([MountKind.Folder, MountKind.Folder, MountKind.Volume, MountKind.Volume], plan.Select(m => m.Kind));
        Assert.True(plan[1].IsGameFiles);

        // Only the volume Docker made for the image is chosen before the administrator does
        Assert.Equal([false, false, false, true], plan.Select(m => m.RemoveByDefault));
    }

    [Fact]
    public void FilesAnotherContainerUsesAreKept()
    {
        var others = new HostUsage([], [
            new MountUse("quake3-dm", true, "/data/Servers/quake3/Base", "/config/Server", false),
            new MountUse("backup", false, "shared-maps", "/backup", true),
        ]);

        var plan = ContainerRemoval.Plan([Mount("/data/Servers/quake3/Base", "/config/Server"), Mount("shared-maps", "/maps")], others, "/data/Servers");

        Assert.Equal("Also used by quake3-dm.", plan[0].CantRemove);
        Assert.Equal("Also used by backup (stopped).", plan[1].CantRemove);
        Assert.All(plan, m => Assert.False(m.RemoveByDefault));
    }

    [Fact]
    public void OnlyFoldersInsideTheDataFolderAreRemoved()
    {
        var plan = ContainerRemoval.Plan([Mount("/srv/game", "/config"), Mount("/data/Servers", "/all")], Nobody, "/data/Servers");

        Assert.All(plan, m => Assert.Contains("only removed inside the host's data folder, /data/Servers", m.CantRemove));
    }

    [Fact]
    public void WithoutADataFolderNoFolderIsRemoved()
    {
        var plan = ContainerRemoval.Plan([Mount("/data/Servers/quake3/quake3-ctf", "/config"), Mount("quake3-ctf-config", "/x")], Nobody, null);

        Assert.Contains("the host has none", plan[0].CantRemove);
        Assert.True(plan[1].CanRemove);
    }

    [Theory]
    [InlineData("/data/Servers/quake3", "/data/Servers", true)]
    [InlineData("/data/Servers/quake3/Base/", "/data/Servers/", true)]
    [InlineData("/data/Servers", "/data/Servers", false)]
    [InlineData("/data/Servers2/quake3", "/data/Servers", false)]
    [InlineData("/data/Servers/../etc", "/data/Servers", false)]
    [InlineData("/data/Servers/./x", "/data/Servers", false)]
    [InlineData("/Data/Servers/quake3", "/data/Servers", false)]
    [InlineData(@"d:\servers\quake3", @"D:\Servers", true)]
    [InlineData(@"D:\Servers\quake3", "D:/Servers", true)]
    [InlineData("/data/Servers/quake3", "", false)]
    public void AFolderIsOnlyInsideAnotherBelowIt(string folder, string parent, bool inside)
    {
        Assert.Equal(inside, ContainerRemoval.IsInside(folder, parent));
    }

    [Theory]
    [InlineData("/data/Servers/quake3/Base", "/data/Servers/quake3", "Base")]
    [InlineData("/data/Servers/quake3-ctf/", "/data/Servers", "quake3-ctf")]
    [InlineData("/quake3", "/", "quake3")]
    [InlineData(@"D:\Servers\quake3", @"D:\Servers", "quake3")]
    [InlineData(@"D:\quake3", @"D:\", "quake3")]
    public void AFolderIsSplitFromWhereItIs(string folder, string parent, string name)
    {
        Assert.Equal((parent, name), ContainerRemoval.Split(folder));
    }

    [Theory]
    [InlineData("/srv/game", true)]
    [InlineData(@"D:\Servers", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData("quake3-base", false)]
    [InlineData(Anonymous, false)]
    public void AFolderIsToldFromAVolume(string source, bool folder)
    {
        Assert.Equal(folder, ContainerRemoval.IsFolder(source));
    }

    [Theory]
    [InlineData(Anonymous, true)]
    [InlineData("quake3-base", false)]
    [InlineData("F00DFEEDBEEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123", false)]
    public void AVolumeDockerMadeIsKnownByItsName(string volume, bool anonymous)
    {
        Assert.Equal(anonymous, ContainerRemoval.IsAnonymous(volume));
    }
}
