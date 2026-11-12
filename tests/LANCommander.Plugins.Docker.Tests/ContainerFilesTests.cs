using LANCommander.Plugins.Docker.Engine;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.Server.Plugins;

namespace LANCommander.Plugins.Docker.Tests;

/// <summary>The engine's files, as far as they can be tried without a Docker daemon.</summary>
public sealed class ContainerFilesTests
{
    private static MountedFolder Mount(string destination, string? local = null) => new(destination, "/data/Servers" + destination, true, local);

    [Theory]
    [InlineData(new string[0], "/data", "/data")]
    [InlineData(new string[0], "/data/", "/data")]
    [InlineData(new string[0], "", "/")]
    [InlineData(new string[0], null, "/")]
    [InlineData(new[] { "/config" }, "/srv", "/config")]
    [InlineData(new[] { "/config", "/config/Server" }, "/srv", "/config")]
    [InlineData(new[] { "/srv/game/maps", "/srv/game/cfg" }, null, "/srv/game")]
    [InlineData(new[] { "/data", "/logs" }, null, "/")]
    public void TheFilesStartAtTheFolderHoldingEveryMountOrTheWorkingDirectory(string[] mounts, string? workingDirectory, string expected) =>
        Assert.Equal(expected, ContainerFiles.RootOf(mounts.Select(m => Mount(m)).ToList(), workingDirectory));

    [Fact]
    public void APathIsFoundInTheDeepestMountHoldingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "lancommander-test-" + Guid.NewGuid().ToString("N"));
        var own = Path.Combine(root, "quake3", "dm1");
        var game = Path.Combine(root, "quake3", "Base");

        MountedFolder[] mounts = [Mount("/config", own), Mount("/config/Server", game), Mount("/data"), Mount("/srv/a/b", own)];

        Assert.Equal(new FileLocation.OnHost(own), ContainerFiles.Locate("/config", mounts));
        Assert.Equal(new FileLocation.OnHost(Path.Combine(own, "Overlay", "server.cfg")), ContainerFiles.Locate("/config/Overlay/server.cfg", mounts));
        Assert.Equal(new FileLocation.OnHost(Path.Combine(game, "baseq3")), ContainerFiles.Locate("/config/Server/baseq3", mounts));
        Assert.Equal(new FileLocation.OnHost(Path.Combine(own, "Servers")), ContainerFiles.Locate("/config/Servers", mounts));

        // A volume, or a folder LANCommander can't reach
        Assert.Equal(FileLocation.InContainer.Instance, ContainerFiles.Locate("/data/x", mounts));

        // The folders leading to mounts hold only them
        var between = Assert.IsType<FileLocation.Between>(ContainerFiles.Locate("/", mounts));
        Assert.Equal(["config", "data", "srv"], between.Children);
        Assert.Equal(["b"], Assert.IsType<FileLocation.Between>(ContainerFiles.Locate("/srv/a", mounts)).Children);

        Assert.Equal(FileLocation.InContainer.Instance, ContainerFiles.Locate("/etc", mounts));
    }

    [Fact]
    public void AFolderOnTheHostIsListedWithTheMountsBelowIt()
    {
        var folder = Directory.CreateTempSubdirectory("lancommander-test-");

        try
        {
            Directory.CreateDirectory(Path.Combine(folder.FullName, "Overlay"));
            File.WriteAllText(Path.Combine(folder.FullName, "server.cfg"), "hostname LAN\n");

            var files = ContainerFiles.ListLocal(folder.FullName, "/config", ["Overlay", "Server"]);

            Assert.Equal(["Overlay", "Server", "server.cfg"], files.Select(f => f.Name).Order(StringComparer.Ordinal));
            Assert.Equal(13, files.Single(f => f.Name == "server.cfg").Size);
            Assert.True(files.Single(f => f.Name == "Server").IsDirectory);

            Assert.Throws<DirectoryNotFoundException>(() => ContainerFiles.ListLocal(Path.Combine(folder.FullName, "missing"), "/config/missing", []));
            Assert.Throws<IOException>(() => ContainerFiles.ListLocal(Path.Combine(folder.FullName, "server.cfg"), "/config/server.cfg", []));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ServersSavedWithAFilesFolderStillLoad()
    {
        var settings = ServerEngineSettings.Read<DockerServerSettings>("""{ "HostId": "2f0c6c3e-58a4-4c55-9a43-2d7a2b1e9b10", "ContainerId": "quake3-dm", "FilesPath": "/config/Merged" }""");

        Assert.Equal("quake3-dm", settings.ContainerId);
    }

    [Fact]
    public void BindsAreReadAsWritten()
    {
        var sources = ContainerFiles.WrittenSources(
        [
            "/data/Servers/quake3/dm1:/config",
            @"D:\Servers\quake3\Base:/config/Server:ro",
            "C:/Servers/x:/x/",
            "maps:/maps",
        ]);

        Assert.Equal("/data/Servers/quake3/dm1", sources["/config"]);
        Assert.Equal(@"D:\Servers\quake3\Base", sources["/config/Server"]);
        Assert.Equal("C:/Servers/x", sources["/x"]);
        Assert.Equal("maps", sources["/maps"]);
    }

    [Theory]
    [InlineData("/data", "", "/data")]
    [InlineData("/data", "cfg/server.cfg", "/data/cfg/server.cfg")]
    [InlineData("/", "", "/")]
    [InlineData("/", "etc/hosts", "/etc/hosts")]
    public void PathsAreUnderTheFolder(string root, string path, string expected) =>
        Assert.Equal(expected, ContainerFiles.InContainer(root, path));

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("cfg/../../etc")]
    [InlineData("/etc")]
    public void PathsCantLeaveTheFolder(string path) =>
        Assert.Throws<ArgumentException>(() => ContainerFiles.InContainer("/data", path));

    [Fact]
    public void AListingIsReadAsStatWritesIt()
    {
        var files = ContainerFiles.ParseListing(
            "directory/4096/1761930245/maps\n" +
            "regular file/15/1761930245/server.cfg\n" +
            "regular empty file/0/1761930245/.hidden\n" +
            "regular file/12/1761930245/name with spaces\r\n" +
            "garbled line\n");

        Assert.Equal(
        [
            new ServerEngineFile("maps", true, null, new DateTime(2025, 10, 31, 17, 4, 5, DateTimeKind.Utc)),
            new ServerEngineFile("server.cfg", false, 15, new DateTime(2025, 10, 31, 17, 4, 5, DateTimeKind.Utc)),
            new ServerEngineFile(".hidden", false, 0, new DateTime(2025, 10, 31, 17, 4, 5, DateTimeKind.Utc)),
            new ServerEngineFile("name with spaces", false, 12, new DateTime(2025, 10, 31, 17, 4, 5, DateTimeKind.Utc)),
        ], files);
    }

    [Fact]
    public void FailuresAreReportedAsTheServerExpects()
    {
        ContainerFiles.ThrowIfFailed(0, "", "/data/x", directory: false);

        Assert.Throws<FileNotFoundException>(() => ContainerFiles.ThrowIfFailed(2, "", "/data/x", directory: false));
        Assert.Throws<DirectoryNotFoundException>(() => ContainerFiles.ThrowIfFailed(2, "", "/data/x", directory: true));
        Assert.Throws<IOException>(() => ContainerFiles.ThrowIfFailed(3, "It is a folder.", "/data/x", directory: false));
        Assert.Contains("sh", Assert.Throws<DockerHostException>(() => ContainerFiles.ThrowIfFailed(127, "sh: not found", "/data/x", directory: false)).Message);
        Assert.Contains("Permission denied", Assert.Throws<IOException>(() => ContainerFiles.ThrowIfFailed(1, "cat: can't open: Permission denied", "/data/x", directory: false)).Message);
    }
}
