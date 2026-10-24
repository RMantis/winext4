using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using WinExt4.Native;

namespace WinExt4.Core;

/// <summary>
/// An open ext2/3/4 filesystem. Thread safe: ext4core serialises every call.
/// Names are passed as raw bytes so that non-UTF-8 names survive round trips.
/// </summary>
public sealed unsafe class Ext4Volume : IDisposable
{
    public const uint RootIno = 2;

    private IntPtr _fs;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Ext4Source Source { get; }
    public bool ReadWrite { get; }

    private Ext4Volume(IntPtr fs, Ext4Source source, bool rw)
    {
        _fs = fs;
        Source = source;
        ReadWrite = rw;
    }

    public static Ext4Volume Open(Ext4Source source, bool readWrite)
    {
        int err = Ext4Native.e4_open(source.Device, source.Offset, source.Length,
            readWrite ? Ext4Native.OpenRw : 0, out IntPtr fs);
        Ext4Exception.Check(err, readWrite ? "Apertura in scrittura" : "Apertura");
        return new Ext4Volume(fs, source, readWrite);
    }

    private IntPtr Fs => _fs != IntPtr.Zero ? _fs : throw new ObjectDisposedException(nameof(Ext4Volume));

    public bool IsOpen => _fs != IntPtr.Zero;

    public void Dispose()
    {
        IntPtr fs = Interlocked.Exchange(ref _fs, IntPtr.Zero);
        if (fs != IntPtr.Zero)
            Ext4Exception.Check(Ext4Native.e4_close(fs), "Chiusura");
    }

    // ------------------------------------------------------------------
    // names

    public static byte[] Encode(string name) => Encoding.UTF8.GetBytes(name);

    private static byte[] Z(byte[] name)
    {
        var z = new byte[name.Length + 1];
        name.CopyTo(z, 0);
        return z;
    }

    private static byte[] Z(string s) => Z(Encode(s));

    // ------------------------------------------------------------------
    // metadata

    public Ext4VolumeInfo GetInfo()
    {
        Ext4Exception.Check(Ext4Native.e4_get_info(Fs, out var i));
        Ext4Native.e4_statfs(Fs, out _, out _, out ulong avail);
        return new Ext4VolumeInfo
        {
            Label = Ext4Native.Utf8(i.label, 20),
            Uuid = Ext4Native.Utf8(i.uuid, 40),
            LastMountedOn = Ext4Native.Utf8(i.last_mounted, 68),
            BlockSize = i.block_size,
            TotalBytes = i.blocks_count * i.block_size,
            FreeBytes = avail,
            ReservedBytes = i.reserved_blocks * i.block_size,
            InodesCount = i.inodes_count,
            FreeInodes = i.free_inodes,
            ReadWrite = i.read_write != 0,
            NeedsRecovery = i.needs_recovery != 0,
            JournalReplayed = i.journal_replayed != 0,
            HasJournal = i.has_journal != 0,
            Clean = (i.state & 1) != 0,
            HasErrors = (i.state & 2) != 0,
            RwBlocker = Ext4Native.Utf8(i.rw_blocker, 160),
            Warnings = Ext4Native.Utf8(i.warnings, 256),
            LastMount = i.mount_time > 0 ? DateTimeOffset.FromUnixTimeSeconds(i.mount_time).LocalDateTime : null,
            LastWrite = i.write_time > 0 ? DateTimeOffset.FromUnixTimeSeconds(i.write_time).LocalDateTime : null,
            FeatureCompat = i.feature_compat,
            FeatureIncompat = i.feature_incompat,
            FeatureRoCompat = i.feature_ro_compat,
        };
    }

    public (ulong Total, ulong Free, ulong Available) StatFs()
    {
        Ext4Exception.Check(Ext4Native.e4_statfs(Fs, out ulong t, out ulong f, out ulong a));
        return (t, f, a);
    }

    public void Flush() => Ext4Exception.Check(Ext4Native.e4_flush(Fs), "Scrittura su disco");

    /// <summary>Path lookup ("/a/b"); intermediate symlinks are followed.</summary>
    public uint Lookup(string path)
    {
        byte[] z = Z(path);
        uint ino;
        fixed (byte* p = z)
            Ext4Exception.Check(Ext4Native.e4_lookup(Fs, p, out ino));
        return ino;
    }

    public bool TryLookup(string path, out uint ino)
    {
        byte[] z = Z(path);
        fixed (byte* p = z)
            return Ext4Native.e4_lookup(Fs, p, out ino) == 0;
    }

    public int TryLookupChild(uint dir, byte[] name, out uint ino)
    {
        byte[] z = Z(name);
        fixed (byte* p = z)
            return Ext4Native.e4_lookup_child(Fs, dir, p, out ino);
    }

    public uint LookupChild(uint dir, byte[] name)
    {
        Ext4Exception.Check(TryLookupChild(dir, name, out uint ino));
        return ino;
    }

    /// <summary>Resolves a symlink (relative to <paramref name="dir"/>); returns 0 if dangling.</summary>
    public uint Follow(uint dir, uint ino)
    {
        return Ext4Native.e4_follow(Fs, dir, ino, out uint target) == 0 ? target : 0;
    }

    public Ext4Stat Stat(uint ino)
    {
        Ext4Exception.Check(Ext4Native.e4_stat_ino(Fs, ino, out var s));
        return Convert(s);
    }

    public bool TryStat(uint ino, out Ext4Stat? stat)
    {
        if (Ext4Native.e4_stat_ino(Fs, ino, out var s) != 0)
        {
            stat = null;
            return false;
        }
        stat = Convert(s);
        return true;
    }

    private static Ext4Stat Convert(in Ext4Native.Stat s) => new()
    {
        Ino = s.ino,
        Mode = s.mode,
        Uid = s.uid,
        Gid = s.gid,
        Links = s.links,
        Flags = s.flags,
        Size = s.size,
        AllocSize = s.alloc_size,
        AccessTime = FromUnix(s.atime_sec, s.atime_nsec),
        ModifyTime = FromUnix(s.mtime_sec, s.mtime_nsec),
        ChangeTime = FromUnix(s.ctime_sec, s.ctime_nsec),
        CreateTime = s.crtime_sec != 0 || s.crtime_nsec != 0 ? FromUnix(s.crtime_sec, s.crtime_nsec) : null,
    };

    public static DateTime FromUnix(long sec, uint nsec)
    {
        try
        {
            return DateTime.UnixEpoch.AddTicks(sec * TimeSpan.TicksPerSecond + nsec / 100);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.UnixEpoch;
        }
    }

    public static (long Sec, uint Nsec) ToUnix(DateTime utc)
    {
        long ticks = utc.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks;
        long sec = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out long rem);
        if (rem < 0) { sec--; rem += TimeSpan.TicksPerSecond; }
        return (sec, (uint)(rem * 100));
    }

    private sealed class DirCollector
    {
        public readonly List<Ext4DirEntry> Entries = new();
        public bool IncludeDots;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnDirEntry(IntPtr ctx, byte* name, int len, uint ino, int type)
    {
        var c = (DirCollector)GCHandle.FromIntPtr(ctx).Target!;
        if (!c.IncludeDots && (len == 1 && name[0] == '.' || len == 2 && name[0] == '.' && name[1] == '.'))
            return 0;
        var raw = new ReadOnlySpan<byte>(name, len).ToArray();
        string s;
        bool exact = true;
        try
        {
            s = StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            s = Encoding.UTF8.GetString(raw);
            exact = false;
        }
        c.Entries.Add(new Ext4DirEntry
        {
            RawName = raw,
            Name = s,
            NameIsExact = exact,
            Ino = ino,
            Type = type switch
            {
                1 => Ext4FileType.Regular,
                2 => Ext4FileType.Directory,
                3 => Ext4FileType.CharDevice,
                4 => Ext4FileType.BlockDevice,
                5 => Ext4FileType.Fifo,
                6 => Ext4FileType.Socket,
                7 => Ext4FileType.Symlink,
                _ => Ext4FileType.Unknown,
            },
        });
        return 0;
    }

    public List<Ext4DirEntry> ReadDir(uint dir, bool includeDots = false)
    {
        var c = new DirCollector { IncludeDots = includeDots };
        var h = GCHandle.Alloc(c);
        try
        {
            Ext4Exception.Check(Ext4Native.e4_readdir(Fs, dir, &OnDirEntry, GCHandle.ToIntPtr(h)), "Lettura cartella");
        }
        finally
        {
            h.Free();
        }
        // Directories without the filetype feature report Unknown: stat them.
        foreach (var e in c.Entries.Where(e => e.Type == Ext4FileType.Unknown).ToList())
        {
            if (TryStat(e.Ino, out var st))
            {
                int i = c.Entries.IndexOf(e);
                c.Entries[i] = new Ext4DirEntry
                {
                    RawName = e.RawName, Name = e.Name, NameIsExact = e.NameIsExact,
                    Ino = e.Ino, Type = st!.Type,
                };
            }
        }
        return c.Entries;
    }

    public string ReadLink(uint ino)
    {
        var buf = new byte[8192];
        fixed (byte* p = buf)
        {
            Ext4Exception.Check(Ext4Native.e4_readlink(Fs, ino, p, (uint)buf.Length, out uint len));
            return Encoding.UTF8.GetString(buf, 0, (int)len);
        }
    }

    // ------------------------------------------------------------------
    // data

    public int Read(uint ino, ulong offset, IntPtr buffer, uint length)
    {
        Ext4Exception.Check(Ext4Native.e4_read(Fs, ino, offset, (void*)buffer, length, out uint got), "Lettura");
        return (int)got;
    }

    public int Read(uint ino, ulong offset, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
            return Read(ino, offset, (IntPtr)p, (uint)buffer.Length);
    }

    public int Write(uint ino, ulong offset, IntPtr buffer, uint length)
    {
        Ext4Exception.Check(Ext4Native.e4_write(Fs, ino, offset, (void*)buffer, length, out uint written), "Scrittura");
        return (int)written;
    }

    public int Write(uint ino, ulong offset, ReadOnlySpan<byte> buffer)
    {
        fixed (byte* p = buffer)
            return Write(ino, offset, (IntPtr)p, (uint)buffer.Length);
    }

    public void SetSize(uint ino, ulong size) =>
        Ext4Exception.Check(Ext4Native.e4_set_size(Fs, ino, size), "Ridimensionamento file");

    // ------------------------------------------------------------------
    // namespace

    public uint CreateFile(uint dir, byte[] name, uint mode, uint uid, uint gid)
    {
        byte[] z = Z(name);
        uint ino;
        fixed (byte* p = z)
            Ext4Exception.Check(Ext4Native.e4_create(Fs, dir, p, mode, uid, gid, out ino), "Creazione file");
        return ino;
    }

    public uint CreateDirectory(uint dir, byte[] name, uint mode, uint uid, uint gid)
    {
        byte[] z = Z(name);
        uint ino;
        fixed (byte* p = z)
            Ext4Exception.Check(Ext4Native.e4_mkdir(Fs, dir, p, mode, uid, gid, out ino), "Creazione cartella");
        return ino;
    }

    public uint CreateSymlink(uint dir, byte[] name, string target, uint uid, uint gid)
    {
        byte[] z = Z(name), t = Z(target);
        uint ino;
        fixed (byte* p = z)
        fixed (byte* pt = t)
            Ext4Exception.Check(Ext4Native.e4_symlink(Fs, dir, p, pt, uid, gid, out ino), "Creazione link");
        return ino;
    }

    public void Unlink(uint dir, byte[] name)
    {
        byte[] z = Z(name);
        fixed (byte* p = z)
            Ext4Exception.Check(Ext4Native.e4_unlink(Fs, dir, p), "Eliminazione");
    }

    public void RemoveDirectory(uint dir, byte[] name)
    {
        byte[] z = Z(name);
        fixed (byte* p = z)
            Ext4Exception.Check(Ext4Native.e4_rmdir(Fs, dir, p), "Eliminazione cartella");
    }

    public int TryRename(uint srcDir, byte[] srcName, uint dstDir, byte[] dstName, bool replace)
    {
        byte[] a = Z(srcName), b = Z(dstName);
        fixed (byte* pa = a)
        fixed (byte* pb = b)
            return Ext4Native.e4_rename(Fs, srcDir, pa, dstDir, pb, replace ? 1 : 0);
    }

    public void Rename(uint srcDir, byte[] srcName, uint dstDir, byte[] dstName, bool replace) =>
        Ext4Exception.Check(TryRename(srcDir, srcName, dstDir, dstName, replace), "Rinomina");

    public int TrySetAttributes(uint ino, uint mask, uint mode = 0, uint uid = 0, uint gid = 0,
        DateTime? atime = null, DateTime? mtime = null, DateTime? ctime = null, DateTime? crtime = null)
    {
        var t = new Ext4Native.Times();
        if (atime is { } a) { (t.atime_sec, t.atime_nsec) = ToUnix(a); mask |= Ext4Native.SetAtime; }
        if (mtime is { } m) { (t.mtime_sec, t.mtime_nsec) = ToUnix(m); mask |= Ext4Native.SetMtime; }
        if (ctime is { } c) { (t.ctime_sec, t.ctime_nsec) = ToUnix(c); mask |= Ext4Native.SetCtime; }
        if (crtime is { } cr) { (t.crtime_sec, t.crtime_nsec) = ToUnix(cr); mask |= Ext4Native.SetCrtime; }
        return Ext4Native.e4_set_attr(Fs, ino, mask, mode, uid, gid, &t);
    }

    public void SetAttributes(uint ino, uint mask, uint mode = 0, uint uid = 0, uint gid = 0,
        DateTime? atime = null, DateTime? mtime = null, DateTime? ctime = null, DateTime? crtime = null) =>
        Ext4Exception.Check(TrySetAttributes(ino, mask, mode, uid, gid, atime, mtime, ctime, crtime), "Modifica attributi");

    public SuperInfo GetSuper()
    {
        Ext4Exception.Check(Ext4Native.e4_get_super(Fs, out var s));
        return SuperInfo.From(s);
    }

    /// <summary>tune2fs-style change of one superblock field (needs read-write).</summary>
    public void SetSuper(SuperField field, ulong value = 0, string? text = null)
    {
        byte[]? z = text is null ? null : Z(text);
        fixed (byte* p = z)
            Ext4Exception.Check(Ext4Native.e4_set_super(Fs, (int)field, value, p), "Modifica del filesystem");
    }

    /// <summary>Counts files by type scanning the inode tables (fast, no directory walk).</summary>
    public InodeCounts CountInodes(CancellationToken cancel, IProgress<double>? progress = null)
    {
        // Native memory: the scan polls the flag and updates the position while running.
        IntPtr mem = (IntPtr)NativeMemory.AllocZeroed(16);
        try
        {
            uint total = GetInfo().InodesCount;
            using var reg = cancel.Register(() => Marshal.WriteInt32(mem, 1));
            using var timer = progress is null ? null
                : new Timer(_ => progress.Report(total > 0 ? (double)Marshal.ReadInt64(mem, 8) / total : 0), null, 200, 200);
            int err = Ext4Native.e4_count_inodes(Fs, (int*)mem, (ulong*)(mem + 8), out var c);
            cancel.ThrowIfCancellationRequested();
            Ext4Exception.Check(err, "Conteggio dei file");
            return new InodeCounts(c.regular, c.directories, c.symlinks, c.special, c.regular_bytes);
        }
        finally
        {
            NativeMemory.Free((void*)mem);
        }
    }

    public static string Version
    {
        get
        {
            try { return Marshal.PtrToStringUTF8(Ext4Native.e4_version()) ?? "?"; }
            catch (DllNotFoundException) { return "ext4core.dll mancante"; }
        }
    }
}
