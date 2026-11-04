using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using LANCommander.Plugins.Docker.Containers;

namespace LANCommander.Plugins.Docker.Tests;

public sealed class GameFilesArchiveTests
{
    /// <summary>A ZIP of the given entries; a name ending in "/" is a folder, and a mode is kept as a Unix ZIP keeps it.</summary>
    private static ZipArchive Zip(params (string Name, string? Content, int Mode)[] entries)
    {
        var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content, mode) in entries)
            {
                var entry = zip.CreateEntry(name);

                if (mode != 0)
                    entry.ExternalAttributes = mode << 16;

                if (content != null)
                {
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }
            }
        }

        stream.Position = 0;

        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static List<(string Name, TarEntryType Type, string? Content, UnixFileMode Mode)> Read(Stream tar)
    {
        tar.Position = 0;

        var reader = new TarReader(tar);
        var entries = new List<(string, TarEntryType, string?, UnixFileMode)>();

        while (reader.GetNextEntry() is { } entry)
        {
            string? content = null;

            if (entry.DataStream != null)
                content = new StreamReader(entry.DataStream, Encoding.UTF8).ReadToEnd();

            entries.Add((entry.Name, entry.EntryType, content, entry.Mode));
        }

        return entries;
    }

    [Fact]
    public void AZipsFilesBecomeATarsWithTheirContents()
    {
        using var zip = Zip(("System/", null, 0), ("System/DeusEx.exe", "MZ", 0), ("Maps/DXMP_Cathedral.dx", new string('x', 70_000), 0));
        using var tar = new MemoryStream();

        var files = GameFilesArchive.WriteTar(zip, tar);

        Assert.Equal(2, files);

        var entries = Read(tar);

        Assert.Equal(["System/", "System/DeusEx.exe", "Maps/DXMP_Cathedral.dx"], entries.Select(e => e.Name));
        Assert.Equal(TarEntryType.Directory, entries[0].Type);
        Assert.Equal("MZ", entries[1].Content);
        Assert.Equal(70_000, entries[2].Content!.Length);
    }

    [Fact]
    public void ModesAreKeptOrGivenDefaults()
    {
        using var zip = Zip(("server", "#!/bin/sh", 0b111_101_101 | 0x8000), ("readme.txt", "hi", 0), ("data/", null, 0));
        using var tar = new MemoryStream();

        GameFilesArchive.WriteTar(zip, tar);

        var entries = Read(tar).ToDictionary(e => e.Name, e => e.Mode);

        Assert.Equal((UnixFileMode)0b111_101_101, entries["server"]);
        Assert.Equal((UnixFileMode)0b110_100_100, entries["readme.txt"]);
        Assert.Equal((UnixFileMode)0b111_101_101, entries["data/"]);
    }

    [Fact]
    public void EntriesLeavingTheFolderAndLinksAreLeftOut()
    {
        using var zip = Zip(
            ("../escape.txt", "no", 0),
            ("System/../../escape.txt", "no", 0),
            ("/etc/passwd", "no", 0),
            (@"C:\Windows\win.ini", "no", 0),
            ("link", "/etc/shadow", 0xA000 | 0b111_111_111),
            ("System/ok.ini", "yes", 0));
        using var tar = new MemoryStream();

        Assert.Equal(1, GameFilesArchive.WriteTar(zip, tar));
        Assert.Equal(["System/ok.ini"], Read(tar).Select(e => e.Name));
    }

    [Fact]
    public void AGameZippedWithItsFolderIsFound()
    {
        using var wrapped = Zip(("DeusEx/", null, 0), ("DeusEx/System/DeusEx.exe", "MZ", 0), ("DeusEx/Maps/a.dx", "m", 0));
        using var flat = Zip(("System/DeusEx.exe", "MZ", 0), ("Maps/a.dx", "m", 0));
        using var loose = Zip(("readme.txt", "hi", 0), ("System/DeusEx.exe", "MZ", 0));

        Assert.Equal("DeusEx", GameFilesArchive.SingleTopFolder(wrapped));
        Assert.Null(GameFilesArchive.SingleTopFolder(flat));
        Assert.Null(GameFilesArchive.SingleTopFolder(loose));
    }

    [Fact]
    public void AGamesFolderIsStrippedSoItsFilesAreAtTheTop()
    {
        using var zip = Zip(("DeusEx/", null, 0), ("DeusEx/System/", null, 0), ("DeusEx/System/DeusEx.exe", "MZ", 0));
        using var tar = new MemoryStream();

        GameFilesArchive.WriteTar(zip, tar, stripFolder: "DeusEx");

        Assert.Equal(["System/", "System/DeusEx.exe"], Read(tar).Select(e => e.Name));
    }

    [Fact]
    public void EverythingCanBePutInAFolder()
    {
        // A server that keeps the game in its own files, under Server/
        using var zip = Zip(("DeusEx/System/DeusEx.exe", "MZ", 0));
        using var tar = new MemoryStream();

        GameFilesArchive.WriteTar(zip, tar, stripFolder: "DeusEx", into: "Server");

        Assert.Equal(["Server/", "Server/System/DeusEx.exe"], Read(tar).Select(e => e.Name));
    }

    [Theory]
    [InlineData(@"System\DeusEx.exe", "System/DeusEx.exe")]
    [InlineData("System//Maps/", "System/Maps")]
    [InlineData("./System", null)]
    [InlineData("", null)]
    public void EntryNamesAreMadeTarNames(string zipName, string? tarName)
    {
        Assert.Equal(tarName, GameFilesArchive.Normalize(zipName));
    }
}
