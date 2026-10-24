using System.Buffers;
using System.IO;

namespace WinExt4.Core;

/// <summary>A file extracted to %TEMP% so a Windows program can open it.</summary>
public sealed class TempCopy
{
    public required string LocalPath { get; init; }
    public required uint Ino { get; init; }
    public required string ExtPath { get; init; }
    public bool Writable { get; init; }
    /// <summary>mtime of the ext file when it was last synchronised with the copy.</summary>
    public DateTime ExtModified { get; set; }
    /// <summary>Last write time of the local copy when it was last synchronised.</summary>
    public DateTime LocalSynced { get; set; }

    public bool LocallyModified
    {
        get
        {
            try { return File.GetLastWriteTimeUtc(LocalPath) > LocalSynced; }
            catch (IOException) { return false; }
        }
    }
}

/// <summary>
/// Temporary copies of ext files opened with Windows programs. When the
/// volume is writable the copies are editable, and saves are reported through
/// <see cref="Modified"/> so they can be written back.
/// </summary>
public sealed class TempCopies : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinExt4", Guid.NewGuid().ToString("N")[..8]);
    private readonly Dictionary<string, TempCopy> _copies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Timer> _debounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;

    /// <summary>Raised (on a thread-pool thread) when a writable copy was saved.</summary>
    public event Action<TempCopy>? Modified;

    public IReadOnlyCollection<TempCopy> All
    {
        get { lock (_lock) return _copies.Values.ToList(); }
    }

    /// <summary>Extracts the file and starts watching it.</summary>
    public TempCopy Extract(Ext4Volume vol, uint ino, string extPath, string name, CancellationToken cancel = default)
    {
        var st = vol.Stat(ino);
        string dir = Path.Combine(_root, Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        string local = Path.Combine(dir, LinuxNames.ForExport(name));
        byte[] buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            using var fs = new FileStream(local, FileMode.Create, FileAccess.Write);
            ulong off = 0;
            while (off < st.Size)
            {
                cancel.ThrowIfCancellationRequested();
                int n = vol.Read(ino, off, buf.AsSpan(0, 1 << 20));
                if (n <= 0) break;
                fs.Write(buf, 0, n);
                off += (ulong)n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        File.SetLastWriteTimeUtc(local, st.ModifyTime);
        bool writable = vol.ReadWrite;
        if (!writable) File.SetAttributes(local, FileAttributes.ReadOnly);

        var copy = new TempCopy
        {
            LocalPath = local,
            Ino = ino,
            ExtPath = extPath,
            Writable = writable,
            ExtModified = st.ModifyTime,
            LocalSynced = File.GetLastWriteTimeUtc(local),
        };
        lock (_lock)
        {
            _copies[local] = copy;
            EnsureWatcher();
        }
        return copy;
    }

    private void EnsureWatcher()
    {
        if (_watcher is not null) return;
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += (_, e) => Touch(e.FullPath);
        _watcher.Created += (_, e) => Touch(e.FullPath);
        // Many editors save to a temporary name and rename it over the original.
        _watcher.Renamed += (_, e) => Touch(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    private void Touch(string path)
    {
        lock (_lock)
        {
            if (!_copies.TryGetValue(path, out var copy) || !copy.Writable) return;
            if (_debounce.TryGetValue(path, out var t)) t.Change(1500, Timeout.Infinite);
            else _debounce[path] = new Timer(_ => Settle(copy), null, 1500, Timeout.Infinite);
        }
    }

    private void Settle(TempCopy copy)
    {
        lock (_lock)
        {
            if (_debounce.Remove(copy.LocalPath, out var t)) t.Dispose();
        }
        // Wait until the program has finished writing (file no longer locked).
        try
        {
            using var probe = new FileStream(copy.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException)
        {
            Touch(copy.LocalPath);
            return;
        }
        if (copy.LocallyModified) Modified?.Invoke(copy);
    }

    /// <summary>Writes the local copy back into the ext file (keeps owner and permissions).</summary>
    public static void WriteBack(Ext4Volume vol, TempCopy copy)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            using var fs = new FileStream(copy.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            vol.SetSize(copy.Ino, 0);
            ulong off = 0;
            int n;
            while ((n = fs.Read(buf, 0, 1 << 20)) > 0)
            {
                int done = 0;
                while (done < n) done += vol.Write(copy.Ino, off + (ulong)done, buf.AsSpan(done, n - done));
                off += (ulong)n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        var local = File.GetLastWriteTimeUtc(copy.LocalPath);
        vol.TrySetAttributes(copy.Ino, 0, mtime: local);
        vol.Flush();
        copy.LocalSynced = local;
        copy.ExtModified = vol.Stat(copy.Ino).ModifyTime;
    }

    /// <summary>Forgets all copies and deletes what can be deleted (programs may still hold some).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _watcher?.Dispose();
            _watcher = null;
            foreach (var t in _debounce.Values) t.Dispose();
            _debounce.Clear();
            _copies.Clear();
        }
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_root, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => Clear();
}
