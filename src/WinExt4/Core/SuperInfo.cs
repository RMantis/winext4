using System.Text;
using WinExt4.Native;

namespace WinExt4.Core;

public enum SuperField
{
    Label = 1,
    Uuid = 2,
    ReservedBlocks = 3,
    ReservedUid = 4,
    ReservedGid = 5,
    MaxMountCount = 6,
    CheckInterval = 7,
    MountCount = 8,
    Errors = 9,
    DefaultMountOptions = 10,
    LastMounted = 11,
    EnableCsumSeed = 12,
}

public enum UuidChange { Allowed = 0, NeedsCsumSeed = 1, Impossible = 2 }

public sealed class SuperInfo
{
    public DateTime? Created { get; init; }
    public DateTime? LastCheck { get; init; }
    public uint MountCount { get; init; }
    public int MaxMountCount { get; init; }
    public uint CheckIntervalSeconds { get; init; }
    public uint ErrorsBehavior { get; init; }
    public uint DefaultMountOptions { get; init; }
    public uint ReservedUid { get; init; }
    public uint ReservedGid { get; init; }
    public uint CreatorOs { get; init; }
    public string Revision { get; init; } = "";
    public uint BlocksPerGroup { get; init; }
    public uint InodesPerGroup { get; init; }
    public uint GroupCount { get; init; }
    public uint JournalInode { get; init; }
    public ulong JournalSize { get; init; }
    public uint DirectoryCount { get; init; }
    public uint ErrorCount { get; init; }
    public string? FirstError { get; init; }
    public string? LastError { get; init; }
    public ulong KilobytesWritten { get; init; }
    public uint FlexGroupSize { get; init; }
    public string MountOptions { get; init; } = "";
    public string HashSeed { get; init; } = "";
    public bool HasCsumSeed { get; init; }
    public UuidChange UuidChange { get; init; }

    public string CreatorOsText => CreatorOs switch
    {
        0 => "Linux",
        1 => "Hurd",
        2 => "Masix",
        3 => "FreeBSD",
        4 => "Lites",
        _ => $"sconosciuto ({CreatorOs})",
    };

    public string ErrorsText => ErrorsBehavior switch
    {
        1 => "continua",
        2 => "rimonta in sola lettura",
        3 => "panic",
        _ => $"{ErrorsBehavior}",
    };

    // EXT2_DEFM_* bits shown in the dialog
    public static readonly (uint Bit, string Name, string Description)[] MountOptionBits =
    {
        (0x0004, "user_xattr", "attributi estesi degli utenti"),
        (0x0008, "acl", "ACL POSIX"),
        (0x0400, "discard", "TRIM automatico dei blocchi liberati"),
        (0x0100, "nobarrier", "senza barriere di scrittura (sconsigliato)"),
        (0x0200, "block_validity", "controllo extra dei blocchi"),
        (0x0002, "bsdgroups", "gruppo dei nuovi file = gruppo della cartella"),
    };

    internal static unsafe SuperInfo From(in Ext4Native.Super s)
    {
        string? Err(long time, uint ino, ulong block, uint line, byte* func)
        {
            if (time == 0) return null;
            string f = Ext4Native.Utf8(func, 36);
            return $"{DateTimeOffset.FromUnixTimeSeconds(time).LocalDateTime:g} · {f}:{line}" +
                   (ino != 0 ? $" · inode {ino}" : "") + (block != 0 ? $" · blocco {block}" : "");
        }
        fixed (byte* fe = s.first_error_func)
        fixed (byte* le = s.last_error_func)
        fixed (byte* mo = s.mount_opts)
        fixed (byte* hs = s.hash_seed)
            return new SuperInfo
            {
                Created = s.mkfs_time > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.mkfs_time).LocalDateTime : null,
                LastCheck = s.last_check > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.last_check).LocalDateTime : null,
                MountCount = s.mnt_count,
                MaxMountCount = s.max_mnt_count,
                CheckIntervalSeconds = s.check_interval,
                ErrorsBehavior = s.errors_behavior,
                DefaultMountOptions = s.default_mount_opts,
                ReservedUid = s.reserved_uid,
                ReservedGid = s.reserved_gid,
                CreatorOs = s.creator_os,
                Revision = $"{s.rev_level}.{s.minor_rev}",
                BlocksPerGroup = s.blocks_per_group,
                InodesPerGroup = s.inodes_per_group,
                GroupCount = s.group_count,
                JournalInode = s.journal_inum,
                JournalSize = s.journal_size,
                DirectoryCount = s.dir_count,
                ErrorCount = s.error_count,
                FirstError = Err(s.first_error_time, s.first_error_ino, s.first_error_block, s.first_error_line, fe),
                LastError = Err(s.last_error_time, s.last_error_ino, s.last_error_block, s.last_error_line, le),
                KilobytesWritten = s.kbytes_written,
                FlexGroupSize = s.log_groups_per_flex is > 0 and < 32 ? 1u << (int)s.log_groups_per_flex : 0,
                MountOptions = Ext4Native.Utf8(mo, 68),
                HashSeed = Ext4Native.Utf8(hs, 40),
                HasCsumSeed = s.has_csum_seed != 0,
                UuidChange = (UuidChange)s.uuid_change,
            };
    }
}

public sealed record InodeCounts(ulong Files, ulong Directories, ulong Symlinks, ulong Special, ulong FileBytes);

/// <summary>Feature flags with short Italian descriptions (for tooltips).</summary>
public static class Ext4Features
{
    private static readonly (int Word, uint Bit, string Name, string Text)[] All =
    {
        (0, 0x0004, "has_journal", "journal (ext3/ext4)"),
        (0, 0x0008, "ext_attr", "attributi estesi"),
        (0, 0x0010, "resize_inode", "spazio riservato per ingrandire il filesystem"),
        (0, 0x0020, "dir_index", "cartelle indicizzate (htree) per molti file"),
        (0, 0x0200, "sparse_super2", "solo due copie di backup del superblocco"),
        (0, 0x0400, "fast_commit", "journal con commit veloci"),
        (0, 0x0800, "stable_inodes", "numeri di inode stabili (impedisce di cambiare UUID)"),
        (1, 0x0002, "filetype", "tipo di file nelle voci di cartella"),
        (1, 0x0004, "needs_recovery", "journal da riprodurre"),
        (1, 0x0008, "journal_dev", "dispositivo di journal esterno"),
        (1, 0x0010, "meta_bg", "gruppi di metadati"),
        (1, 0x0040, "extent", "extent (ext4): file grandi e meno frammentati"),
        (1, 0x0080, "64bit", "indirizzi a 64 bit: filesystem oltre 16 TB"),
        (1, 0x0100, "mmp", "protezione da montaggio multiplo"),
        (1, 0x0200, "flex_bg", "gruppi di blocchi flessibili"),
        (1, 0x0400, "ea_inode", "attributi estesi grandi in inode dedicati"),
        (1, 0x1000, "dirdata", "dati nelle voci di cartella"),
        (1, 0x2000, "metadata_csum_seed", "seme dei checksum fissato: l'UUID si può cambiare"),
        (1, 0x4000, "large_dir", "cartelle oltre 2 GB / htree a 3 livelli"),
        (1, 0x8000, "inline_data", "dati dei file piccoli dentro l'inode"),
        (1, 0x10000, "encrypt", "cifratura per cartella (fscrypt)"),
        (1, 0x20000, "casefold", "nomi senza distinzione maiuscole/minuscole"),
        (2, 0x0001, "sparse_super", "copie del superblocco solo in alcuni gruppi"),
        (2, 0x0002, "large_file", "file oltre 2 GB"),
        (2, 0x0008, "huge_file", "file oltre 2 TB"),
        (2, 0x0010, "uninit_bg", "checksum dei gruppi (inizializzazione rapida)"),
        (2, 0x0020, "dir_nlink", "più di 65000 sottocartelle"),
        (2, 0x0040, "extra_isize", "inode estesi (timestamp in nanosecondi)"),
        (2, 0x0100, "quota", "quote disco"),
        (2, 0x0200, "bigalloc", "allocazione a cluster"),
        (2, 0x0400, "metadata_csum", "checksum di tutti i metadati"),
        (2, 0x1000, "read-only", "filesystem di sola lettura"),
        (2, 0x2000, "project", "quote per progetto"),
        (2, 0x8000, "verity", "file verificati (fs-verity)"),
        (2, 0x10000, "orphan_present", "file orfani da ripulire"),
    };

    public static List<(string Name, string Text)> Describe(uint compat, uint incompat, uint roCompat)
    {
        uint[] words = { compat, incompat, roCompat };
        return All.Where(f => (words[f.Word] & f.Bit) != 0).Select(f => (f.Name, f.Text)).ToList();
    }
}
