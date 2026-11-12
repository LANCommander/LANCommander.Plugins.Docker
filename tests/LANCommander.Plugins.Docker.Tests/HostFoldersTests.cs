using LANCommander.Plugins.Docker.Hosts;

namespace LANCommander.Plugins.Docker.Tests;

/// <summary>Finding a Docker host's folders where LANCommander can reach them.</summary>
public sealed class HostFoldersTests
{
    [Theory]
    [InlineData("/data/Servers/quake3/dm1", "/data/Servers", new[] { "quake3", "dm1" })]
    [InlineData("/data/Servers/", "/data/Servers", new string[0])]
    [InlineData(@"D:\Servers\quake3", @"d:\servers", new[] { "quake3" })]
    [InlineData("/data/servers/quake3", "/data/Servers", null)]
    [InlineData("/data/Servers2/quake3", "/data/Servers", null)]
    [InlineData("/data/Servers/../etc", "/data/Servers", null)]
    [InlineData("/srv/game", "/data/Servers", null)]
    public void AFolderIsFoundInsideTheDataFolder(string path, string dataFolder, string[]? expected) =>
        Assert.Equal(expected, HostFolders.RelativeTo(path, dataFolder));

    [Fact]
    public void TheDataFolderIsReachedWhereLANCommanderFindsIt()
    {
        var local = Directory.CreateTempSubdirectory("lancommander-test-");

        try
        {
            var host = new DockerHost { Address = "tcp://docker-host:2375", DataFolder = "/data/Servers", LocalDataFolder = local.FullName };

            Assert.Equal(Path.Combine(local.FullName, "quake3", "dm1"), HostFolders.LocalPathOf(host, "/data/Servers/quake3/dm1"));
            Assert.Null(HostFolders.LocalPathOf(host, "/srv/game"));

            // Without saying where, only a local daemon's data folder is taken to be at the same path
            Assert.Null(HostFolders.LocalPathOf(new DockerHost { Address = "tcp://docker-host:2375", DataFolder = local.FullName }, Path.Combine(local.FullName, "x")));
            Assert.Equal(Path.Combine(local.FullName, "x"), HostFolders.LocalPathOf(new DockerHost { Address = DockerHost.DefaultAddress, DataFolder = local.FullName }, Path.Combine(local.FullName, "x")));
            Assert.Null(HostFolders.LocalPathOf(new DockerHost { Address = DockerHost.DefaultAddress, DataFolder = Path.Combine(local.FullName, "missing") }, Path.Combine(local.FullName, "missing", "x")));

            // Without a data folder, nothing is
            Assert.Null(HostFolders.LocalPathOf(new DockerHost { Address = DockerHost.DefaultAddress, LocalDataFolder = local.FullName }, "/data/Servers/x"));
        }
        finally
        {
            local.Delete(recursive: true);
        }
    }

    [Fact]
    public void LinksAreOnlyFollowedInsideTheFolder()
    {
        var root = Directory.CreateTempSubdirectory("lancommander-test-");
        var outside = Directory.CreateTempSubdirectory("lancommander-test-");

        try
        {
            var inside = Directory.CreateDirectory(Path.Combine(root.FullName, "Overlay"));

            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root.FullName, "current"), "Overlay");
                Directory.CreateSymbolicLink(Path.Combine(root.FullName, "escape"), outside.FullName);
                Directory.CreateSymbolicLink(Path.Combine(inside.FullName, "up"), "..");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Windows without developer mode can't make links; nothing to try then
                return;
            }

            Assert.Equal(Path.Combine(inside.FullName, "server.cfg"), HostFolders.Confine(root.FullName, ["current", "server.cfg"]));
            Assert.Equal(Path.Combine(root.FullName, "new", "file"), HostFolders.Confine(root.FullName, ["new", "file"]));
            Assert.Equal(Path.Combine(root.FullName, "Overlay"), HostFolders.Confine(root.FullName, ["Overlay", "up", "Overlay"]));

            Assert.Throws<UnauthorizedAccessException>(() => HostFolders.Confine(root.FullName, ["escape", "secret"]));
            Assert.Throws<UnauthorizedAccessException>(() => HostFolders.Confine(root.FullName, [".."]));
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }
}
