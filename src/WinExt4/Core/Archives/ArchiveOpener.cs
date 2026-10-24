using System.IO;

namespace WinExt4.Core.Archives;

/// <summary>A file inside an archive that may be a disk/filesystem image.</summary>
public sealed record ArchiveMember(string Name, IRandomAccess Access);

/// <summary>Members of an opened archive; owns the underlying accesses.</summary>
public sealed class ArchiveContents : IDisposable
{
    public required string Path { get; init; }
    public string Kind { get; set; } = "";
    public List<ArchiveMember> Members { get; } = new();
    public List<string> Skipped { get; } = new();
    internal List<IDisposable> Owned { get; } = new();

    public void Dispose()
    {
        foreach (var d in Owned) d.Dispose();
        Owned.Clear();
    }
}

/// <summary>
/// Opens .tar, .tar.gz/.tgz, .img.gz, .img.xz, .tar.xz, .img.zst, .tar.zst and
/// .zip so the images inside can be used without extracting them.
/// </summary>
public static class ArchiveOpener
{
    private const long MinImage = 64 * 1024;

    public static bool IsArchive(string path)
    {
        try
        {
            if (Zip.Looks(path)) return true;
            var f = ImageFormats.Detect(path);
            if (f is ImageFormat.Gzip or ImageFormat.Zstd or ImageFormat.Xz) return true;
            using var plain = new FileSliceAccess(path, 0, new FileInfo(path).Length);
            return Tar.Looks(plain);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static ArchiveContents Open(string path, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        string name = System.IO.Path.GetFileName(path);
        if (Zip.Looks(path)) return OpenZip(path, progress, cancel);

        var format = ImageFormats.Detect(path);
        IRandomAccess inner;
        string kind;
        var result = new ArchiveContents { Path = path };
        switch (format)
        {
            case ImageFormat.Gzip:
                inner = DeflateAccess.Build(path, 0, true, ArchiveStore.Key(path, "gz"), progress, cancel);
                kind = "gzip";
                break;
            case ImageFormat.Xz:
            {
                var xz = XzAccess.Open(path);
                if (xz.BlockCount >= 2) { inner = xz; kind = $"xz, {xz.BlockCount} blocchi"; }
                else
                {
                    xz.Dispose();
                    inner = CachedAccess.Create(path, ArchiveStore.Key(path, "xz"), progress, cancel);
                    kind = "xz a blocco unico (decompresso nella cache)";
                }
                break;
            }
            case ImageFormat.Zstd:
                if (ZstdAccess.TryOpen(path) is { } z) { inner = z; kind = "zstd a frame"; }
                else
                {
                    inner = CachedAccess.Create(path, ArchiveStore.Key(path, "zst"), progress, cancel);
                    kind = "zstd (decompresso nella cache)";
                }
                break;
            default:
                inner = new FileSliceAccess(path, 0, new FileInfo(path).Length);
                kind = "tar";
                break;
        }
        result.Owned.Add(inner);
        if (Tar.Looks(inner))
        {
            foreach (var e in Tar.List(inner, cancel))
            {
                if (e.Size < MinImage) continue;
                result.Members.Add(new ArchiveMember(e.Name, new SubRangeAccess(inner, e.Offset, e.Size)));
            }
            kind = format == ImageFormat.Raw ? "tar" : $"tar ({kind})";
        }
        else if (format == ImageFormat.Raw)
        {
            result.Dispose();
            throw new InvalidDataException($"{name}: non è un archivio riconosciuto.");
        }
        else
        {
            // a single compressed image: disk.img.gz -> disk.img
            result.Members.Add(new ArchiveMember(System.IO.Path.GetFileName(Sidecar.RawName(path)), inner));
        }
        result.Kind = kind;
        return result;
    }

    private static ArchiveContents OpenZip(string path, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        var result = new ArchiveContents { Path = path, Kind = "zip" };
        foreach (var e in Zip.List(path))
        {
            if (e.Size < MinImage) continue;
            if (e.Method == 0)
            {
                var a = new FileSliceAccess(path, e.Offset, e.Size);
                result.Owned.Add(a);
                result.Members.Add(new ArchiveMember(e.Name, a));
            }
            else if (e.Method == 8)
            {
                var a = DeflateAccess.Build(path, e.Offset, false, ArchiveStore.Key(path, "zip:" + e.Name), progress, cancel);
                result.Owned.Add(a);
                result.Members.Add(new ArchiveMember(e.Name, a));
            }
            else
            {
                result.Skipped.Add($"{e.Name}: metodo di compressione zip {e.Method} non supportato");
            }
        }
        return result;
    }
}
