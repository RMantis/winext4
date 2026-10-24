namespace WinExt4.Core;

public enum Ext4FileType
{
    Unknown = 0,
    Regular = 1,
    Directory = 2,
    CharDevice = 3,
    BlockDevice = 4,
    Fifo = 5,
    Socket = 6,
    Symlink = 7,
}

public static class LinuxMode
{
    public const uint S_IFMT = 0xF000;
    public const uint S_IFSOCK = 0xC000;
    public const uint S_IFLNK = 0xA000;
    public const uint S_IFREG = 0x8000;
    public const uint S_IFBLK = 0x6000;
    public const uint S_IFDIR = 0x4000;
    public const uint S_IFCHR = 0x2000;
    public const uint S_IFIFO = 0x1000;

    public static Ext4FileType TypeOf(uint mode) => (mode & S_IFMT) switch
    {
        S_IFREG => Ext4FileType.Regular,
        S_IFDIR => Ext4FileType.Directory,
        S_IFLNK => Ext4FileType.Symlink,
        S_IFCHR => Ext4FileType.CharDevice,
        S_IFBLK => Ext4FileType.BlockDevice,
        S_IFIFO => Ext4FileType.Fifo,
        S_IFSOCK => Ext4FileType.Socket,
        _ => Ext4FileType.Unknown,
    };

    /// <summary>"drwxr-xr-x" style string.</summary>
    public static string ToString(uint mode)
    {
        char t = TypeOf(mode) switch
        {
            Ext4FileType.Directory => 'd',
            Ext4FileType.Symlink => 'l',
            Ext4FileType.CharDevice => 'c',
            Ext4FileType.BlockDevice => 'b',
            Ext4FileType.Fifo => 'p',
            Ext4FileType.Socket => 's',
            _ => '-',
        };
        var c = new char[10];
        c[0] = t;
        const string rwx = "rwxrwxrwx";
        for (int i = 0; i < 9; i++)
            c[i + 1] = (mode & (1u << (8 - i))) != 0 ? rwx[i] : '-';
        if ((mode & 0x800) != 0) c[3] = c[3] == 'x' ? 's' : 'S';
        if ((mode & 0x400) != 0) c[6] = c[6] == 'x' ? 's' : 'S';
        if ((mode & 0x200) != 0) c[9] = c[9] == 'x' ? 't' : 'T';
        return new string(c);
    }
}

public sealed class Ext4Stat
{
    public uint Ino { get; init; }
    public uint Mode { get; init; }
    public uint Uid { get; init; }
    public uint Gid { get; init; }
    public uint Links { get; init; }
    public uint Flags { get; init; }
    public ulong Size { get; init; }
    public ulong AllocSize { get; init; }
    public DateTime AccessTime { get; init; }
    public DateTime ModifyTime { get; init; }
    public DateTime ChangeTime { get; init; }
    public DateTime? CreateTime { get; init; }

    public Ext4FileType Type => LinuxMode.TypeOf(Mode);
    public bool IsDirectory => Type == Ext4FileType.Directory;
    public bool IsSymlink => Type == Ext4FileType.Symlink;
    public bool IsRegular => Type == Ext4FileType.Regular;
    public uint Permissions => Mode & 0xFFF;
}

public sealed class Ext4DirEntry
{
    public required byte[] RawName { get; init; }
    public required string Name { get; init; }
    /// <summary>False when the on-disk name is not valid UTF-8 (Name is then lossy).</summary>
    public bool NameIsExact { get; init; }
    public uint Ino { get; init; }
    public Ext4FileType Type { get; init; }
}

public sealed class Ext4VolumeInfo
{
    public string Label { get; init; } = "";
    public string Uuid { get; init; } = "";
    public string LastMountedOn { get; init; } = "";
    public uint BlockSize { get; init; }
    public ulong TotalBytes { get; init; }
    public ulong FreeBytes { get; init; }
    public ulong ReservedBytes { get; init; }
    public uint InodesCount { get; init; }
    public uint FreeInodes { get; init; }
    public bool ReadWrite { get; init; }
    public bool NeedsRecovery { get; init; }
    public bool JournalReplayed { get; init; }
    public bool HasJournal { get; init; }
    public bool Clean { get; init; }
    public bool HasErrors { get; init; }
    public string RwBlocker { get; init; } = "";
    public string Warnings { get; init; } = "";
    public DateTime? LastMount { get; init; }
    public DateTime? LastWrite { get; init; }
    public uint FeatureCompat { get; init; }
    public uint FeatureIncompat { get; init; }
    public uint FeatureRoCompat { get; init; }

    public string Kind =>
        (FeatureIncompat & (0x40 | 0x80 | 0x200)) != 0 ? "ext4" : HasJournal ? "ext3" : "ext2";
}
