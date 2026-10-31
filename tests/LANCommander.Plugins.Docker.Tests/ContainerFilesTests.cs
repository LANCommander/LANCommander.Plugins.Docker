using LANCommander.Plugins.Docker.Engine;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.Server.Plugins;

namespace LANCommander.Plugins.Docker.Tests;

/// <summary>The engine's files, as far as they can be tried without a Docker daemon.</summary>
public sealed class ContainerFilesTests
{
    [Theory]
    [InlineData(null, "/data", "/data")]
    [InlineData(null, "/data/", "/data")]
    [InlineData(null, "", "/")]
    [InlineData(null, null, "/")]
    [InlineData("/srv/game", "/data", "/srv/game")]
    [InlineData("srv/game/", "/data", "/srv/game")]
    [InlineData("  ", "/data", "/data")]
    [InlineData("/", "/data", "/")]
    public void TheFilesAreUnderTheChosenFolderOrTheWorkingDirectory(string? filesPath, string? workingDirectory, string expected) =>
        Assert.Equal(expected, ContainerFiles.RootOf(new DockerServerSettings { FilesPath = filesPath }, workingDirectory));

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
