using System.Buffers;
using System.IO;

namespace WinExt4.Core;

public enum ConflictChoice { Overwrite, Skip, Cancel }

public sealed class TransferProgress
{
    public string CurrentItem { get; set; } = "";
    public long BytesDone { get; set; }
    public long BytesTotal { get; set; }
    public int FilesDone { get; set; }
    public int Skipped { get; set; }
    public List<string> Problems { get; } = new();
}

/// <summary>Recursive copy between the ext filesystem and Windows folders.</summary>
public sealed class FileTransfer
{
    private const int ChunkSize = 1 << 20;

    private readonly Ext4Volume _vol;
    private readonly AppSettings _settings;
    private readonly IProgress<TransferProgress>? _progress;
    private readonly CancellationToken _cancel;
    private readonly TransferProgress _state = new();
    private DateTime _lastReport;

    /// <summary>Asked when a destination already exists; receives the name.</summary>
    public Func<string, ConflictChoice>? OnConflict { get; set; }

    public FileTransfer(Ext4Volume vol, AppSettings settings, IProgress<TransferProgress>? progress, CancellationToken cancel)
    {
        _vol = vol;
        _settings = settings;
        _progress = progress;
        _cancel = cancel;
    }

    public TransferProgress State => _state;

    private void Report(bool force = false)
    {
        if (_progress is null) return;
        var now = DateTime.UtcNow;
        if (!force && (now - _lastReport).TotalMilliseconds < 100) return;
        _lastReport = now;
        _progress.Report(_state);
    }

    // ------------------------------------------------------------------
    // ext -> Windows

    public void Export(uint parentDir, IEnumerable<Ext4DirEntry> entries, string destFolder)
    {
        var list = entries.ToList();
        _state.BytesTotal = list.Sum(e => (long)MeasureExt(parentDir, e.Ino, 0));
        foreach (var e in list)
            ExportOne(parentDir, e, destFolder, 0);
        Report(true);
    }

    private ulong MeasureExt(uint dir, uint ino, int depth)
    {
        if (depth > 64 || !_vol.TryStat(ino, out var st)) return 0;
        if (st!.IsSymlink)
        {
            uint t = _vol.Follow(dir, ino);
            return t != 0 && _vol.TryStat(t, out var ts) && ts!.IsRegular ? ts.Size : 0;
        }
        if (!st.IsDirectory) return st.IsRegular ? st.Size : 0;
        ulong sum = 0;
        foreach (var c in _vol.ReadDir(ino))
            sum += MeasureExt(ino, c.Ino, depth + 1);
        return sum;
    }

    private void ExportOne(uint dir, Ext4DirEntry entry, string destFolder, int depth)
    {
        _cancel.ThrowIfCancellationRequested();
        string target = Path.Combine(destFolder, LinuxNames.ForExport(entry.Name));
        _state.CurrentItem = entry.Name;
        Report();

        var st = _vol.Stat(entry.Ino);
        uint ino = entry.Ino;
        if (st.IsSymlink)
        {
            // Follow links to regular files; directory links could loop and
            // absolute ones point at the Linux root, so those are skipped.
            uint t = _vol.Follow(dir, ino);
            if (t == 0 || !_vol.TryStat(t, out var ts) || !ts!.IsRegular)
            {
                _state.Skipped++;
                _state.Problems.Add($"{entry.Name}: link simbolico non copiato");
                return;
            }
            ino = t;
            st = ts;
        }

        if (st.IsDirectory)
        {
            if (depth > 64) return;
            Directory.CreateDirectory(target);
            foreach (var child in _vol.ReadDir(ino))
                ExportOne(ino, child, target, depth + 1);
            TrySetTimes(() => Directory.SetLastWriteTimeUtc(target, st.ModifyTime));
            return;
        }
        if (!st.IsRegular)
        {
            _state.Skipped++;
            _state.Problems.Add($"{entry.Name}: file speciale ({st.Type}) non copiato");
            return;
        }

        if (File.Exists(target) && !Confirm(target)) return;

        byte[] buf = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                ulong off = 0;
                while (off < st.Size)
                {
                    _cancel.ThrowIfCancellationRequested();
                    int n = _vol.Read(ino, off, buf.AsSpan(0, ChunkSize));
                    if (n <= 0) break;
                    fs.Write(buf, 0, n);
                    off += (ulong)n;
                    _state.BytesDone += n;
                    Report();
                }
            }
            TrySetTimes(() => File.SetLastWriteTimeUtc(target, st.ModifyTime));
            _state.FilesDone++;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private bool Confirm(string name)
    {
        var choice = OnConflict?.Invoke(name) ?? ConflictChoice.Skip;
        if (choice == ConflictChoice.Cancel) throw new OperationCanceledException();
        if (choice == ConflictChoice.Skip) { _state.Skipped++; return false; }
        return true;
    }

    private static void TrySetTimes(Action a)
    {
        try { a(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------
    // Windows -> ext

    public void Import(IEnumerable<string> localPaths, uint destDir)
    {
        var list = localPaths.ToList();
        _state.BytesTotal = list.Sum(MeasureLocal);
        foreach (var p in list)
            ImportOne(p, destDir);
        _vol.Flush();
        Report(true);
    }

    private static long MeasureLocal(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (Directory.Exists(path))
                return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception) { }
        return 0;
    }

    private void ImportOne(string path, uint destDir)
    {
        _cancel.ThrowIfCancellationRequested();
        string winName = Path.GetFileName(path.TrimEnd('\\', '/'));
        byte[] name = Ext4Volume.Encode(LinuxNames.ToLinux(winName));
        _state.CurrentItem = winName;
        Report();

        var parent = _vol.Stat(destDir);
        var (uid, gid) = _settings.OwnerFor(parent);

        if (Directory.Exists(path))
        {
            uint dir;
            if (_vol.TryLookupChild(destDir, name, out uint existing) == 0)
            {
                if (!_vol.Stat(existing).IsDirectory)
                    throw new Ext4Exception(Errno.EEXIST, winName);
                dir = existing;
            }
            else
            {
                dir = _vol.CreateDirectory(destDir, name, _settings.DirMode, uid, gid);
            }
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                ImportOne(child, dir);
            var di = new DirectoryInfo(path);
            _vol.TrySetAttributes(dir, 0, mtime: di.LastWriteTimeUtc, atime: di.LastAccessTimeUtc);
            return;
        }

        var fi = new FileInfo(path);
        uint ino;
        if (_vol.TryLookupChild(destDir, name, out uint found) == 0)
        {
            var st = _vol.Stat(found);
            if (st.IsDirectory) throw new Ext4Exception(Errno.EISDIR, winName);
            if (!Confirm(winName)) return;
            ino = found;
            _vol.SetSize(ino, 0);
        }
        else
        {
            uint mode = _settings.FileMode;
            if (fi.IsReadOnly) mode &= ~0x92u; // drop the w bits
            ino = _vol.CreateFile(destDir, name, mode, uid, gid);
        }

        byte[] buf = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            ulong off = 0;
            int n;
            while ((n = fs.Read(buf, 0, ChunkSize)) > 0)
            {
                _cancel.ThrowIfCancellationRequested();
                int done = 0;
                while (done < n)
                    done += _vol.Write(ino, off + (ulong)done, buf.AsSpan(done, n - done));
                off += (ulong)n;
                _state.BytesDone += n;
                Report();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        _vol.TrySetAttributes(ino, 0, mtime: fi.LastWriteTimeUtc, atime: fi.LastAccessTimeUtc);
        _state.FilesDone++;
    }

    // ------------------------------------------------------------------
    // delete

    public void Delete(uint dir, IEnumerable<Ext4DirEntry> entries)
    {
        foreach (var e in entries.ToList())
            DeleteOne(dir, e, 0);
        _vol.Flush();
        Report(true);
    }

    private void DeleteOne(uint dir, Ext4DirEntry e, int depth)
    {
        _cancel.ThrowIfCancellationRequested();
        _state.CurrentItem = e.Name;
        Report();
        var st = _vol.Stat(e.Ino);
        if (st.IsDirectory)
        {
            if (depth > 256) throw new Ext4Exception(Errno.ELOOP, e.Name);
            foreach (var child in _vol.ReadDir(e.Ino))
                DeleteOne(e.Ino, child, depth + 1);
            _vol.RemoveDirectory(dir, e.RawName);
        }
        else
        {
            _vol.Unlink(dir, e.RawName);
        }
        _state.FilesDone++;
    }
}
