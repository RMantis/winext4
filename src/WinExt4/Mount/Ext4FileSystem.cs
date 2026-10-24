using System.IO;
using System.Security.AccessControl;
using Fsp;
using WinExt4.Core;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace WinExt4.Mount;

/// <summary>
/// Exposes an <see cref="Ext4Volume"/> as a Windows drive through WinFsp
/// (a signed driver, so no test-signing is needed).
///
/// Mapping rules (shown to the user before mounting):
///  * Linux permissions are not translated to Windows ACLs: everybody gets
///    full access, and only the owner's "w" bit is surfaced as READONLY.
///  * New files/directories get uid/gid and mode from <see cref="AppSettings"/>.
///  * Symlinks are followed (absolute ones relative to the ext root);
///    dangling links show up as empty read-only files.
///  * Characters Windows forbids are mapped into U+F000.. (WSL convention).
/// </summary>
public sealed class Ext4FileSystem : FileSystemBase
{
    private const uint FILE_ATTRIBUTE_READONLY = 0x01;
    private const uint FILE_ATTRIBUTE_HIDDEN = 0x02;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
    private const ulong AllocationUnit = 4096;

    private readonly Ext4Volume _vol;
    private readonly AppSettings _settings;
    private readonly byte[] _securityDescriptor;
    private readonly string _label;
    private int _dirty;
    private Timer? _flushTimer;

    public Ext4FileSystem(Ext4Volume vol, AppSettings settings)
    {
        _vol = vol;
        _settings = settings;
        var sd = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)");
        _securityDescriptor = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_securityDescriptor, 0);
        _label = vol.GetInfo().Label;
    }

    private bool ReadOnly => !_vol.ReadWrite;

    /// <summary>Handle state for one open file or directory.</summary>
    private sealed class Node
    {
        public uint Ino;          // inode we read/write (symlinks resolved)
        public bool IsDirectory;
        public bool Dangling;     // symlink pointing nowhere
        public List<(string Name, FileInfo Info)>? DirCache;
    }

    // ------------------------------------------------------------------
    // lifetime

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        var info = _vol.GetInfo();
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 1000;
        host.CaseSensitiveSearch = true;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.ReparsePoints = false;
        host.NamedStreams = false;
        host.PostCleanupWhenModifiedOnly = true;
        host.PassQueryDirectoryPattern = false;
        host.FileSystemName = info.Kind.ToUpperInvariant();
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = (uint)info.Uuid.GetHashCode();
        return STATUS_SUCCESS;
    }

    public override int Mounted(object host)
    {
        // libext2fs keeps bitmaps and group descriptors in memory; push them
        // out regularly so that an unplugged disk loses as little as possible.
        _flushTimer = new Timer(_ => FlushIfDirty(), null, 3000, 3000);
        return STATUS_SUCCESS;
    }

    public override void Unmounted(object host)
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        FlushIfDirty();
    }

    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    private void FlushIfDirty()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 1)
        {
            try { _vol.Flush(); } catch (Exception) { MarkDirty(); }
        }
    }

    public override int ExceptionHandler(Exception ex)
    {
        return ex is Ext4Exception e ? ToStatus(e.Code) : STATUS_UNEXPECTED_IO_ERROR;
    }

    private static int ToStatus(int errno) => errno switch
    {
        Errno.ENOENT => STATUS_OBJECT_NAME_NOT_FOUND,
        Errno.ENOTDIR => STATUS_NOT_A_DIRECTORY,
        Errno.EISDIR => STATUS_FILE_IS_A_DIRECTORY,
        Errno.EEXIST => STATUS_OBJECT_NAME_COLLISION,
        Errno.ENOTEMPTY => STATUS_DIRECTORY_NOT_EMPTY,
        Errno.ENOSPC => STATUS_DISK_FULL,
        Errno.EROFS => STATUS_MEDIA_WRITE_PROTECTED,
        Errno.EACCES => STATUS_ACCESS_DENIED,
        Errno.ENAMETOOLONG => STATUS_NAME_TOO_LONG,
        Errno.EINVAL => STATUS_INVALID_PARAMETER,
        Errno.EFBIG => STATUS_FILE_TOO_LARGE,
        Errno.EBUSY => STATUS_SHARING_VIOLATION,
        Errno.ELOOP => STATUS_OBJECT_PATH_NOT_FOUND,
        Errno.ENOMEM => STATUS_INSUFFICIENT_RESOURCES,
        _ => STATUS_FILE_CORRUPT_ERROR,
    };

    // ------------------------------------------------------------------
    // path resolution

    private static string[] Split(string fileName) =>
        fileName.Split('\\', StringSplitOptions.RemoveEmptyEntries);

    private static byte[] LinuxName(string winComponent) =>
        Ext4Volume.Encode(LinuxNames.ToLinux(winComponent));

    /// <summary>
    /// Walks a Windows path. Returns 0 or an errno; on success <paramref name="ino"/>
    /// is the final inode (symlinks resolved when <paramref name="followLast"/>).
    /// </summary>
    private int Resolve(string fileName, bool followLast, out uint ino, out uint parent, out bool dangling)
    {
        ino = Ext4Volume.RootIno;
        parent = Ext4Volume.RootIno;
        dangling = false;
        var parts = Split(fileName);
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            parent = ino;
            int err = _vol.TryLookupChild(ino, LinuxName(parts[i]), out uint child);
            if (err != 0)
                return err == Errno.ENOENT && !last ? Errno.ENOTDIR + 1000 : err; // path vs name
            if (!_vol.TryStat(child, out var st)) return Errno.EIO;
            if (st!.IsSymlink && (!last || followLast))
            {
                uint target = _vol.Follow(ino, child);
                if (target == 0)
                {
                    if (!last) return Errno.ENOTDIR + 1000;
                    dangling = true;
                    ino = child;
                    return 0;
                }
                child = target;
                if (!last && !_vol.Stat(child).IsDirectory) return Errno.ENOTDIR + 1000;
            }
            else if (!last && !st.IsDirectory)
            {
                return Errno.ENOTDIR + 1000;
            }
            ino = child;
        }
        return 0;
    }

    private static int ResolveStatus(int err) =>
        err == Errno.ENOTDIR + 1000 ? STATUS_OBJECT_PATH_NOT_FOUND : ToStatus(err);

    /// <summary>Parent directory inode and Linux name of the last component.</summary>
    private int ResolveParent(string fileName, out uint parentIno, out byte[] name)
    {
        var parts = Split(fileName);
        name = Array.Empty<byte>();
        parentIno = Ext4Volume.RootIno;
        if (parts.Length == 0) return Errno.EINVAL;
        string dirPath = string.Join('\\', parts, 0, parts.Length - 1);
        int err = Resolve(dirPath, true, out parentIno, out _, out _);
        if (err != 0) return err;
        if (!_vol.Stat(parentIno).IsDirectory) return Errno.ENOTDIR + 1000;
        name = LinuxName(parts[^1]);
        return 0;
    }

    // ------------------------------------------------------------------
    // FileInfo

    private static ulong ToFileTime(DateTime utc)
    {
        long ft = utc.ToFileTimeUtc();
        return ft > 0 ? (ulong)ft : 0;
    }

    private FileInfo MakeInfo(uint ino, string? name, bool dangling = false)
    {
        var info = new FileInfo();
        if (dangling)
        {
            info.FileAttributes = FILE_ATTRIBUTE_READONLY;
            info.IndexNumber = ino;
            info.HardLinks = 1;
            return info;
        }
        var st = _vol.Stat(ino);
        uint attrs = st.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : 0;
        if ((st.Mode & 0x80) == 0 && !st.IsDirectory) attrs |= FILE_ATTRIBUTE_READONLY; // no u+w
        if (_settings.HideDotFiles && name is { Length: > 1 } && name[0] == '.' && name != "..")
            attrs |= FILE_ATTRIBUTE_HIDDEN;
        if (attrs == 0) attrs = FILE_ATTRIBUTE_NORMAL;
        info.FileAttributes = attrs;
        info.FileSize = st.IsDirectory ? 0 : st.Size;
        info.AllocationSize = st.IsDirectory ? 0 : (st.Size + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
        info.CreationTime = ToFileTime(st.CreateTime ?? st.ModifyTime);
        info.LastAccessTime = ToFileTime(st.AccessTime);
        info.LastWriteTime = ToFileTime(st.ModifyTime);
        info.ChangeTime = ToFileTime(st.ChangeTime);
        info.IndexNumber = ino;
        info.HardLinks = st.Links;
        return info;
    }

    private FileInfo InfoOf(Node n) => MakeInfo(n.Ino, null, n.Dangling);

    // ------------------------------------------------------------------
    // volume

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        var (total, _, avail) = _vol.StatFs();
        volumeInfo.TotalSize = total;
        volumeInfo.FreeSize = ReadOnly ? 0 : avail;
        volumeInfo.SetVolumeLabel(_label.Length > 0 ? _label : "ext");
        return STATUS_SUCCESS;
    }

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        GetVolumeInfo(out volumeInfo);
        return STATUS_INVALID_DEVICE_REQUEST;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        int err = Resolve(fileName, true, out uint ino, out _, out bool dangling);
        if (err != 0) return ResolveStatus(err);
        fileAttributes = MakeInfo(ino, Path.GetFileName(fileName), dangling).FileAttributes;
        if (securityDescriptor is not null)
            securityDescriptor = _securityDescriptor;
        return STATUS_SUCCESS;
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = _securityDescriptor;
        return STATUS_SUCCESS;
    }

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor)
    {
        // Linux ownership/permissions are managed from the WinExt4 window.
        return STATUS_SUCCESS;
    }

    // ------------------------------------------------------------------
    // open / create

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object? fileNode, out object? fileDesc, out FileInfo fileInfo, out string? normalizedName)
    {
        fileNode = fileDesc = null;
        fileInfo = default;
        normalizedName = null;
        int err = Resolve(fileName, true, out uint ino, out _, out bool dangling);
        if (err != 0) return ResolveStatus(err);
        var node = new Node { Ino = ino, Dangling = dangling, IsDirectory = !dangling && _vol.Stat(ino).IsDirectory };
        fileNode = node;
        fileInfo = MakeInfo(ino, Path.GetFileName(fileName), dangling);
        return STATUS_SUCCESS;
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[] securityDescriptor, ulong allocationSize,
        out object? fileNode, out object? fileDesc, out FileInfo fileInfo, out string? normalizedName)
    {
        fileNode = fileDesc = null;
        fileInfo = default;
        normalizedName = null;
        if (ReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;

        int err = ResolveParent(fileName, out uint parent, out byte[] name);
        if (err != 0) return ResolveStatus(err);
        if (_vol.TryLookupChild(parent, name, out _) == 0) return STATUS_OBJECT_NAME_COLLISION;

        var (uid, gid) = _settings.OwnerFor(_vol.Stat(parent));
        bool dir = (createOptions & FILE_DIRECTORY_FILE) != 0;
        uint ino;
        if (dir)
        {
            ino = _vol.CreateDirectory(parent, name, _settings.DirMode, uid, gid);
        }
        else
        {
            uint mode = _settings.FileMode;
            if ((fileAttributes & FILE_ATTRIBUTE_READONLY) != 0) mode &= ~0x92u;
            ino = _vol.CreateFile(parent, name, mode, uid, gid);
        }
        MarkDirty();
        fileNode = new Node { Ino = ino, IsDirectory = dir };
        fileInfo = MakeInfo(ino, null);
        return STATUS_SUCCESS;
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes,
        bool replaceFileAttributes, ulong allocationSize, out FileInfo fileInfo)
    {
        var n = (Node)fileNode;
        fileInfo = default;
        if (ReadOnly || n.Dangling) return STATUS_MEDIA_WRITE_PROTECTED;
        _vol.SetSize(n.Ino, 0);
        if (replaceFileAttributes || (fileAttributes & FILE_ATTRIBUTE_READONLY) != 0)
            ApplyReadOnly(n.Ino, (fileAttributes & FILE_ATTRIBUTE_READONLY) != 0);
        MarkDirty();
        fileInfo = InfoOf(n);
        return STATUS_SUCCESS;
    }

    public override void Cleanup(object fileNode, object fileDesc, string? fileName, uint flags)
    {
        var n = (Node)fileNode;
        n.DirCache = null;
        if ((flags & CleanupDelete) == 0 || fileName is null || ReadOnly) return;
        if (ResolveParent(fileName, out uint parent, out byte[] name) != 0) return;
        try
        {
            if (_vol.TryLookupChild(parent, name, out uint ino) != 0) return;
            if (_vol.Stat(ino).IsDirectory)
                _vol.RemoveDirectory(parent, name);
            else
                _vol.Unlink(parent, name);
            MarkDirty();
        }
        catch (Ext4Exception)
        {
            // Cleanup cannot fail; CanDelete already vetted the request.
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
    }

    // ------------------------------------------------------------------
    // data

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        out uint bytesTransferred)
    {
        var n = (Node)fileNode;
        bytesTransferred = 0;
        if (n.Dangling) return STATUS_END_OF_FILE;
        var st = _vol.Stat(n.Ino);
        if (offset >= st.Size) return STATUS_END_OF_FILE;
        bytesTransferred = (uint)_vol.Read(n.Ino, offset, buffer, length);
        return STATUS_SUCCESS;
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo)
    {
        var n = (Node)fileNode;
        bytesTransferred = 0;
        fileInfo = default;
        if (ReadOnly || n.Dangling) return STATUS_MEDIA_WRITE_PROTECTED;
        ulong size = _vol.Stat(n.Ino).Size;
        if (constrainedIo)
        {
            if (offset >= size)
            {
                fileInfo = InfoOf(n);
                return STATUS_SUCCESS;
            }
            if (offset + length > size) length = (uint)(size - offset);
        }
        if (writeToEndOfFile) offset = size;
        uint done = 0;
        while (done < length)
        {
            int w = _vol.Write(n.Ino, offset + done, buffer + (int)done, length - done);
            if (w <= 0) break;
            done += (uint)w;
        }
        bytesTransferred = done;
        MarkDirty();
        fileInfo = InfoOf(n);
        return STATUS_SUCCESS;
    }

    public override int Flush(object? fileNode, object? fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (!ReadOnly)
        {
            Interlocked.Exchange(ref _dirty, 0);
            _vol.Flush();
        }
        if (fileNode is Node n) fileInfo = InfoOf(n);
        return STATUS_SUCCESS;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = InfoOf((Node)fileNode);
        return STATUS_SUCCESS;
    }

    private void ApplyReadOnly(uint ino, bool readOnly)
    {
        var st = _vol.Stat(ino);
        uint mode = st.Permissions;
        uint newMode = readOnly ? mode & ~0x92u : mode | 0x80u;
        if (newMode != mode)
            _vol.SetAttributes(ino, Native.Ext4Native.SetMode, newMode);
    }

    private static DateTime? FromFileTime(ulong ft) =>
        ft == 0 || ft == ulong.MaxValue ? null : DateTime.FromFileTimeUtc((long)ft);

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes,
        ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
    {
        var n = (Node)fileNode;
        fileInfo = default;
        if (ReadOnly || n.Dangling) return STATUS_MEDIA_WRITE_PROTECTED;
        if (fileAttributes != INVALID_FILE_ATTRIBUTES && !n.IsDirectory)
            ApplyReadOnly(n.Ino, (fileAttributes & FILE_ATTRIBUTE_READONLY) != 0);
        var at = FromFileTime(lastAccessTime);
        var mt = FromFileTime(lastWriteTime);
        var crt = FromFileTime(creationTime);
        if (at is not null || mt is not null || crt is not null)
            _vol.SetAttributes(n.Ino, 0, atime: at, mtime: mt, crtime: crt);
        MarkDirty();
        fileInfo = InfoOf(n);
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize,
        out FileInfo fileInfo)
    {
        var n = (Node)fileNode;
        fileInfo = default;
        if (ReadOnly || n.Dangling) return STATUS_MEDIA_WRITE_PROTECTED;
        ulong size = _vol.Stat(n.Ino).Size;
        // Allocation size changes only matter when they cut into the data.
        if (!setAllocationSize || newSize < size)
        {
            _vol.SetSize(n.Ino, newSize);
            MarkDirty();
        }
        fileInfo = InfoOf(n);
        return STATUS_SUCCESS;
    }

    // ------------------------------------------------------------------
    // namespace

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        var n = (Node)fileNode;
        if (ReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        if (Split(fileName).Length == 0) return STATUS_ACCESS_DENIED;
        // Deleting a symlink to a directory removes the link, so only check
        // real directories for emptiness.
        if (ResolveParent(fileName, out uint parent, out byte[] name) != 0) return STATUS_OBJECT_PATH_NOT_FOUND;
        if (_vol.TryLookupChild(parent, name, out uint ino) != 0) return STATUS_OBJECT_NAME_NOT_FOUND;
        if (_vol.Stat(ino).IsDirectory && _vol.ReadDir(ino).Count > 0) return STATUS_DIRECTORY_NOT_EMPTY;
        return STATUS_SUCCESS;
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName,
        bool replaceIfExists)
    {
        if (ReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        int err = ResolveParent(fileName, out uint srcDir, out byte[] srcName);
        if (err != 0) return ResolveStatus(err);
        err = ResolveParent(newFileName, out uint dstDir, out byte[] dstName);
        if (err != 0) return ResolveStatus(err);
        err = _vol.TryRename(srcDir, srcName, dstDir, dstName, replaceIfExists);
        if (err == Errno.EEXIST) return STATUS_OBJECT_NAME_COLLISION;
        if (err == Errno.ENOTEMPTY || err == Errno.EISDIR) return STATUS_ACCESS_DENIED;
        if (err != 0) return ToStatus(err);
        MarkDirty();
        return STATUS_SUCCESS;
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string? marker,
        ref object? context, out string? fileName, out FileInfo fileInfo)
    {
        var n = (Node)fileNode;
        if (context is null)
        {
            if (n.DirCache is null || marker is null)
                n.DirCache = BuildListing(n.Ino);
            int start = 0;
            if (marker is not null)
            {
                start = n.DirCache.FindIndex(e => string.CompareOrdinal(e.Name, marker) > 0);
                if (start < 0) start = n.DirCache.Count;
            }
            context = new DirCursor { Index = start };
        }
        var cursor = (DirCursor)context;
        if (cursor.Index < n.DirCache!.Count)
        {
            var (name, info) = n.DirCache[cursor.Index++];
            fileName = name;
            fileInfo = info;
            return true;
        }
        fileName = null;
        fileInfo = default;
        return false;
    }

    private sealed class DirCursor
    {
        public int Index;
    }

    private List<(string Name, FileInfo Info)> BuildListing(uint dirIno)
    {
        var list = new List<(string, FileInfo)>();
        var entries = _vol.ReadDir(dirIno, includeDots: true);
        foreach (var e in entries)
        {
            if (!e.NameIsExact) continue; // cannot be addressed from Windows
            string winName = e.Name is "." or ".." ? e.Name : LinuxNames.ToWindows(e.Name);
            if (dirIno == Ext4Volume.RootIno && e.Name == "..") continue;
            try
            {
                uint ino = e.Ino;
                bool dangling = false;
                if (e.Type == Ext4FileType.Symlink)
                {
                    uint t = _vol.Follow(dirIno, ino);
                    if (t == 0) dangling = true; else ino = t;
                }
                list.Add((winName, MakeInfo(ino, e.Name, dangling)));
            }
            catch (Ext4Exception)
            {
                // Unreadable inode: leave it out rather than fail the listing.
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }
}
