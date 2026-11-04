using System.Formats.Tar;
using System.IO.Compression;

namespace LANCommander.Plugins.Docker.Containers;

/// <summary>
/// Turns a ZIP of a game's files into the tar Docker's archive API takes, so it can be put in a
/// container's folder without the container running. Entries that would land outside the folder, and
/// links, are left out.
/// </summary>
public static class GameFilesArchive
{
    // A Unix mode's file type bits, as a ZIP made on Unix keeps them in its external attributes
    private const int TypeMask = 0xF000;
    private const int SymbolicLink = 0xA000;

    /// <summary>The folder every entry of the ZIP is in, if they're all in one: a game zipped with its folder.</summary>
    public static string? SingleTopFolder(ZipArchive zip)
    {
        string? top = null;

        foreach (var entry in zip.Entries)
        {
            var name = Normalize(entry.FullName);

            if (name == null)
                continue;

            var slash = name.IndexOf('/');

            // A file at the top: there's no one folder
            if (slash < 0 && !IsDirectory(entry))
                return null;

            var first = slash < 0 ? name : name[..slash];

            if (top == null)
                top = first;
            else if (top != first)
                return null;
        }

        return top;
    }

    /// <summary>
    /// Writes the ZIP's files as a tar, without <paramref name="stripFolder"/> where everything is in
    /// it. Returns how many files it holds.
    /// </summary>
    /// <param name="progress">Told each file's name as it's written.</param>
    /// <param name="into">A folder to put everything in, made first if it isn't there.</param>
    public static int WriteTar(ZipArchive zip, Stream output, string? stripFolder = null, IProgress<string>? progress = null, string? into = null)
    {
        using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        var files = 0;
        var prefix = string.IsNullOrEmpty(stripFolder) ? null : stripFolder.TrimEnd('/') + "/";
        var folder = string.IsNullOrEmpty(into) ? "" : into.Trim('/') + "/";

        if (folder.Length > 0)
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, folder) { Mode = (UnixFileMode)0b111_101_101 });

        foreach (var entry in zip.Entries)
        {
            var name = Normalize(entry.FullName);

            if (name == null || IsLink(entry))
                continue;

            if (prefix != null)
            {
                if (name == prefix.TrimEnd('/'))
                    continue;

                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                name = name[prefix.Length..];
            }

            name = folder + name;

            var mode = (entry.ExternalAttributes >> 16) & 0xFFF;
            var modified = entry.LastWriteTime;

            if (IsDirectory(entry))
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name + "/")
                {
                    Mode = (UnixFileMode)(mode != 0 ? mode : 0b111_101_101),
                    ModificationTime = modified,
                });

                continue;
            }

            using var data = entry.Open();

            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                Mode = (UnixFileMode)(mode != 0 ? mode : 0b110_100_100),
                ModificationTime = modified,
                DataStream = new KnownLengthStream(data, entry.Length),
            });

            files++;
            progress?.Report(name);
        }

        return files;
    }

    /// <summary>
    /// The entry's path with "/" between folders and none at either end; null for one that would
    /// leave the folder it's put in, or names nothing.
    /// </summary>
    internal static string? Normalize(string fullName)
    {
        var name = fullName.Replace('\\', '/').Trim('/');

        if (name.Length == 0)
            return null;

        // An absolute path, a drive, or a way out
        if (fullName.StartsWith('/') || fullName.StartsWith('\\') || (name.Length > 1 && name[1] == ':'))
            return null;

        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Any(segment => segment is ".." or "."))
            return null;

        return string.Join('/', segments);
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    private static bool IsLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & TypeMask) == SymbolicLink;

    /// <summary>
    /// A ZIP entry's data, which can't seek, with the length its entry says it has: a tar records a
    /// file's size before its data, which TarWriter otherwise only takes from a stream that can seek.
    /// </summary>
    private sealed class KnownLengthStream(Stream inner, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set
            {
                // TarWriter only asks to start where it already is
                if (value != _position)
                    throw new NotSupportedException();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);

            _position += read;

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => length + offset,
            };

            if (target != _position)
                throw new NotSupportedException();

            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
