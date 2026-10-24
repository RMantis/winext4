using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using WinExt4.Native;

namespace WinExt4.Core;

public sealed record DraftInfo(string Path, ulong SourceLength, DateTime Created, ulong ChangedChunks, ulong FileBytes,
    int ApplyState, string Label, string Uuid, string SourceDescription)
{
    public ulong ChangedBytes => ChangedChunks * 4096;
}

/// <summary>
/// Draft files (.wx4draft): copy-on-write deltas of a filesystem. Kept by
/// default in %LOCALAPPDATA%\WinExt4\drafts; they can be copied elsewhere
/// and resumed on another PC with the same source.
/// </summary>
public static unsafe class Drafts
{
    public const string Extension = ".wx4draft";

    public static string Dir
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinExt4", "drafts");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string NewPath(Ext4Source s)
    {
        string name = s.Label.Length > 0 ? s.Label : s.Uuid.Length > 0 ? s.Uuid[..8] : "ext";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(Dir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}{Extension}");
    }

    /// <summary>Stored in the draft; for images it starts with the full path, used to find them again.</summary>
    public static string Describe(Ext4Source s) =>
        s.IsInArchive ? $"{s.ImagePath} › {s.ArchiveMember}" + (s.PartitionNumber > 0 ? $" (partizione {s.PartitionNumber})" : "")
        : s.IsImage ? s.ImagePath + (s.PartitionNumber > 0 ? $" (partizione {s.PartitionNumber})" : "")
        : $"{s.Model} · disco {s.DiskNumber}, partizione {s.PartitionNumber}";

    /// <summary>The native description of where <paramref name="s"/> lives.</summary>
    internal static Ext4Native.Source SourceOf(Ext4Source s, ref GCHandle handle, out MemoryHandle pin)
    {
        var src = new Ext4Native.Source { offset = s.Offset, length = s.Length };
        if (s.Virtual is { } v)
        {
            if (!handle.IsAllocated) handle = GCHandle.Alloc(v);
            src.read = &Ext4Native.VirtualRead;
            src.read_ctx = GCHandle.ToIntPtr(handle);
            src.read_size = (ulong)v.Length;
            pin = default;
        }
        else
        {
            pin = (s.Device + "\0").AsMemory().Pin();
            src.path = (char*)pin.Pointer;
        }
        return src;
    }

    /// <summary>The filesystem as the draft sees it, for hashing/exporting.</summary>
    /// <remarks>Use a view (<c>OpenDraft(..., view: true)</c>) so the image is marked clean.</remarks>
    public static RawTarget ExportTarget(Ext4Volume v)
    {
        if (v.ReadWrite) v.Flush();
        var a = v.DeviceAccess();
        var s = v.Source;
        string what = s.IsImage ? Path.GetFileName(s.ImagePath) + (s.IsInArchive ? $" › {s.ArchiveMember}" : "") : $"disco {s.DiskNumber}";
        return new RawTarget("", 0, (ulong)a.Length, $"Bozza di {what}, partizione {s.PartitionNumber}") { Virtual = a };
    }

    public static DraftInfo? Info(string path)
    {
        if (Ext4Native.e4_draft_get_info(path, out var i) != 0) return null;
        return new DraftInfo(path, i.source_length, DateTimeOffset.FromUnixTimeSeconds(i.created).LocalDateTime,
            i.changed_chunks, i.file_bytes, (int)i.apply_state, Ext4Native.Utf8(i.label, 20), Ext4Native.Utf8(i.uuid, 40),
            Ext4Native.Wide(i.source, 260));
    }

    /// <summary>True when <paramref name="path"/> was made from <paramref name="s"/> and the source has not changed.</summary>
    public static bool Matches(string path, Ext4Source s)
    {
        GCHandle h = default;
        try
        {
            var src = SourceOf(s, ref h, out var pin);
            using (pin)
                return Ext4Native.e4_draft_check(path, in src) == 0;
        }
        finally
        {
            if (h.IsAllocated) h.Free();
        }
    }

    /// <summary>Unapplied drafts in the drafts folder that belong to <paramref name="s"/>.</summary>
    public static List<DraftInfo> FindFor(Ext4Source s)
    {
        var list = new List<DraftInfo>();
        if (!s.IsExt) return list;
        foreach (var f in Directory.EnumerateFiles(Dir, "*" + Extension))
        {
            var i = Info(f);
            if (i is null || i.ApplyState == 2 || i.SourceLength != (s.Length > 0 ? s.Length : i.SourceLength)) continue;
            if (s.Uuid.Length > 0 && i.Uuid.Length > 0 && !string.Equals(s.Uuid, i.Uuid, StringComparison.OrdinalIgnoreCase)) continue;
            if (Matches(f, s)) list.Add(i);
        }
        return list.OrderByDescending(i => i.Created).ToList();
    }

    /// <summary>Writes the draft into its source (locked for writing). Re-runnable if interrupted.</summary>
    public static void Apply(string path, Ext4Source s, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        var info = Info(path) ?? throw new Ext4Exception(Errno.EINVAL, "Bozza");
        IntPtr mem = (IntPtr)NativeMemory.AllocZeroed(16);
        GCHandle h = default;
        try
        {
            using var reg = cancel.Register(() => Marshal.WriteInt32(mem, 1));
            var state = new JobProgress { Phase = "Applicazione della bozza", Total = (long)info.ChangedChunks };
            using var timer = progress is null ? null : new Timer(_ =>
            {
                state.Done = Marshal.ReadInt64(mem, 8);
                progress.Report(state);
            }, null, 200, 200);
            var src = SourceOf(s, ref h, out var pin);
            int err;
            using (pin)
                err = Ext4Native.e4_draft_apply(path, in src, (int*)mem, (ulong*)(mem + 8));
            cancel.ThrowIfCancellationRequested();
            Ext4Exception.Check(err, "Applicazione della bozza");
        }
        finally
        {
            if (h.IsAllocated) h.Free();
            NativeMemory.Free((void*)mem);
        }
    }
}

/// <summary>What a draft changed, by comparing the original tree with the draft's.</summary>
public static class DraftDiff
{
    public enum Kind { Added, Removed, Modified }

    public sealed record Change(Kind Kind, string Path, bool IsDirectory, ulong Size);

    public static List<Change> Compare(Ext4Volume original, Ext4Volume draft, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        var changes = new List<Change>();
        var state = new JobProgress { Phase = "Confronto" };
        Walk(original, Ext4Volume.RootIno, draft, Ext4Volume.RootIno, "", changes, state, progress, cancel, 0);
        return changes;
    }

    private static void Walk(Ext4Volume a, uint aDir, Ext4Volume b, uint bDir, string path, List<Change> changes,
        JobProgress state, IProgress<JobProgress>? progress, CancellationToken cancel, int depth)
    {
        if (depth > 128) return;
        cancel.ThrowIfCancellationRequested();
        var left = a.ReadDir(aDir).ToDictionary(e => e.Name, StringComparer.Ordinal);
        var right = b.ReadDir(bDir).ToDictionary(e => e.Name, StringComparer.Ordinal);
        state.Done++;
        state.Phase = path.Length == 0 ? "/" : path;
        progress?.Report(state);
        foreach (var (name, re) in right.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string p = path + "/" + name;
            var rs = b.Stat(re.Ino);
            if (!left.TryGetValue(name, out var le))
            {
                changes.Add(new Change(Kind.Added, p, rs.IsDirectory, rs.Size));
                continue;
            }
            var ls = a.Stat(le.Ino);
            if (ls.IsDirectory && rs.IsDirectory)
            {
                Walk(a, le.Ino, b, re.Ino, p, changes, state, progress, cancel, depth + 1);
                if (ls.Permissions != rs.Permissions || ls.Uid != rs.Uid || ls.Gid != rs.Gid)
                    changes.Add(new Change(Kind.Modified, p, true, 0));
            }
            else if (ls.Type != rs.Type || ls.Size != rs.Size || ls.ModifyTime != rs.ModifyTime || ls.ChangeTime != rs.ChangeTime ||
                     le.Ino != re.Ino)
            {
                changes.Add(new Change(Kind.Modified, p, rs.IsDirectory, rs.Size));
            }
        }
        foreach (var (name, le) in left.OrderBy(k => k.Key, StringComparer.Ordinal))
            if (!right.ContainsKey(name))
            {
                var ls = a.Stat(le.Ino);
                changes.Add(new Change(Kind.Removed, path + "/" + name, ls.IsDirectory, ls.Size));
            }
    }
}
