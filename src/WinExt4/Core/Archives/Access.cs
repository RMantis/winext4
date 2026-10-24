using System.IO;

namespace WinExt4.Core.Archives;

/// <summary>Random read access to the decompressed content of something (thread safe).</summary>
public interface IRandomAccess : IDisposable
{
    long Length { get; }

    /// <summary>Fills <paramref name="dst"/> from <paramref name="pos"/>; bytes past the end read as zero.</summary>
    void Read(long pos, Span<byte> dst);
}

public static class RandomAccessExtensions
{
    /// <summary>Array overload (scripts and callers without spans).</summary>
    public static void ReadInto(this IRandomAccess a, long pos, byte[] buffer, int offset, int count) =>
        a.Read(pos, buffer.AsSpan(offset, count));
}

/// <summary>A byte range of a plain file (tar/zip stored members, cached decompression).</summary>
public sealed class FileSliceAccess : IRandomAccess
{
    private readonly FileStream _fs;
    private readonly long _offset;
    private readonly object _lock = new();

    public long Length { get; }

    public FileSliceAccess(string path, long offset, long length)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        _offset = offset;
        Length = length;
    }

    public void Read(long pos, Span<byte> dst)
    {
        int n = (int)Math.Clamp(Length - pos, 0, dst.Length);
        lock (_lock)
        {
            _fs.Position = _offset + pos;
            int done = 0;
            while (done < n)
            {
                int r = _fs.Read(dst[done..n]);
                if (r <= 0) break;
                done += r;
            }
            dst[done..].Clear();
        }
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>A range of another access (a member inside a decompressed tar).</summary>
public sealed class SubRangeAccess : IRandomAccess
{
    private readonly IRandomAccess _inner;
    private readonly long _offset;

    public long Length { get; }

    public SubRangeAccess(IRandomAccess inner, long offset, long length)
    {
        _inner = inner;
        _offset = offset;
        Length = length;
    }

    public void Read(long pos, Span<byte> dst)
    {
        int n = (int)Math.Clamp(Length - pos, 0, dst.Length);
        if (n > 0) _inner.Read(_offset + pos, dst[..n]);
        dst[n..].Clear();
    }

    /// <summary>The inner access is shared between members and owned by the archive.</summary>
    public void Dispose()
    {
    }
}

/// <summary>Tiny LRU of decoded segments.</summary>
internal sealed class SegmentCache
{
    private readonly int _capacity;
    private readonly LinkedList<(long Key, byte[] Data)> _items = new();

    public SegmentCache(int capacity) => _capacity = capacity;

    public byte[]? Get(long key)
    {
        for (var n = _items.First; n is not null; n = n.Next)
            if (n.Value.Key == key)
            {
                _items.Remove(n);
                _items.AddFirst(n);
                return n.Value.Data;
            }
        return null;
    }

    public void Put(long key, byte[] data)
    {
        _items.AddFirst((key, data));
        while (_items.Count > _capacity) _items.RemoveLast();
    }
}

/// <summary>Index and cache files under %LOCALAPPDATA%\WinExt4.</summary>
public static class ArchiveStore
{
    public static string Dir(string kind)
    {
        string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinExt4", kind);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Identity of an archive (size, mtime, first and last 64 KB) plus a sub-key.</summary>
    public static string Key(string path, string sub)
    {
        var fi = new FileInfo(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = fi.OpenRead();
        var buf = new byte[65536];
        int n = fs.Read(buf);
        sha.TransformBlock(buf, 0, n, null, 0);
        if (fi.Length > buf.Length)
        {
            fs.Seek(-Math.Min(buf.Length, fi.Length - n), SeekOrigin.End);
            n = fs.Read(buf);
            sha.TransformBlock(buf, 0, n, null, 0);
        }
        var meta = System.Text.Encoding.UTF8.GetBytes($"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{sub}");
        sha.TransformFinalBlock(meta, 0, meta.Length);
        return Convert.ToHexString(sha.Hash!)[..32];
    }

    /// <summary>Deletes cache files not used for <paramref name="days"/> days.</summary>
    public static void Cleanup(int days = 14)
    {
        foreach (var kind in new[] { "cache", "index" })
        {
            try
            {
                foreach (var f in new DirectoryInfo(Dir(kind)).EnumerateFiles())
                    if (f.LastAccessTimeUtc < DateTime.UtcNow.AddDays(-days) && f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-days))
                        f.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
