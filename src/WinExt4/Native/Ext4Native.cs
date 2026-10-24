using System.Runtime.InteropServices;

namespace WinExt4.Native;

// Mirrors native/include/ext4core.h. Keep the struct layouts in sync.
internal static unsafe class Ext4Native
{
    private const string Dll = "ext4core.dll";

    public const uint OpenRw = 0x1;
    public const uint OpenNoRecovery = 0x2;

    public const uint SetMode = 0x01;
    public const uint SetUid = 0x02;
    public const uint SetGid = 0x04;
    public const uint SetAtime = 0x08;
    public const uint SetMtime = 0x10;
    public const uint SetCtime = 0x20;
    public const uint SetCrtime = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
        public fixed byte label[20];
        public fixed byte uuid[40];
        public fixed byte last_mounted[68];
        public uint block_size;
        public uint inode_size;
        public ulong blocks_count;
        public ulong free_blocks;
        public ulong reserved_blocks;
        public uint inodes_count;
        public uint free_inodes;
        public uint feature_compat;
        public uint feature_incompat;
        public uint feature_ro_compat;
        public uint state;
        public int read_write;
        public int needs_recovery;
        public int journal_replayed;
        public int has_journal;
        public long mount_time;
        public long write_time;
        public fixed byte rw_blocker[160];
        public fixed byte warnings[256];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Stat
    {
        public uint ino;
        public uint mode;
        public uint uid;
        public uint gid;
        public uint links;
        public uint flags;
        public ulong size;
        public ulong alloc_size;
        public long atime_sec, mtime_sec, ctime_sec, crtime_sec;
        public uint atime_nsec, mtime_nsec, ctime_nsec, crtime_nsec;
        public uint generation;
        public uint reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Times
    {
        public long atime_sec, mtime_sec, ctime_sec, crtime_sec;
        public uint atime_nsec, mtime_nsec, ctime_nsec, crtime_nsec;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Part
    {
        public fixed char device[96];
        public fixed char lock_path[96];
        public ulong offset;
        public ulong length;
        public int disk_number;
        public int partition_number;
        public int bus_type;
        public int removable;
        public int is_ext;
        public int ext_version;
        public fixed byte label[20];
        public fixed byte uuid[40];
        public fixed byte model[128];
        public ulong disk_size;
        public fixed char container[96];
        public int part_style;
        public int mbr_type;
        public fixed byte gpt_type[40];
        public fixed byte gpt_id[40];
        public fixed byte gpt_name[80];
        public ulong gpt_attributes;
        public uint gpt_status;
        public fixed byte disk_id[40];
        public uint sector_size;
        public uint reserved;
    }

    public const uint EditMbrType = 0x1;
    public const uint EditGptType = 0x2;
    public const uint EditGptName = 0x4;
    public const uint EditGptAttributes = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    public struct PartEdit
    {
        public uint mask;
        public int mbr_type;
        public fixed byte gpt_type[40];
        public fixed byte gpt_name[80];
        public ulong gpt_attributes;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    public static extern int e4_edit_partition(string container, int partitionNumber, in PartEdit edit);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    public static extern int e4_open(string path, ulong offset, ulong length, uint flags, out IntPtr fs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_close(IntPtr fs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_get_info(IntPtr fs, out Info info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_flush(IntPtr fs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_lookup(IntPtr fs, byte* path, out uint ino);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_lookup_child(IntPtr fs, uint dir, byte* name, out uint ino);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_follow(IntPtr fs, uint dir, uint ino, out uint target);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_stat_ino(IntPtr fs, uint ino, out Stat st);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_readdir(IntPtr fs, uint dir,
        delegate* unmanaged[Cdecl]<IntPtr, byte*, int, uint, int, int> cb, IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_readlink(IntPtr fs, uint ino, byte* buf, uint bufSize, out uint len);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_statfs(IntPtr fs, out ulong total, out ulong free, out ulong avail);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_read(IntPtr fs, uint ino, ulong offset, void* buf, uint len, out uint got);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_write(IntPtr fs, uint ino, ulong offset, void* buf, uint len, out uint written);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_set_size(IntPtr fs, uint ino, ulong size);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_create(IntPtr fs, uint dir, byte* name, uint mode, uint uid, uint gid, out uint ino);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_mkdir(IntPtr fs, uint dir, byte* name, uint mode, uint uid, uint gid, out uint ino);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_symlink(IntPtr fs, uint dir, byte* name, byte* target, uint uid, uint gid, out uint ino);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_unlink(IntPtr fs, uint dir, byte* name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_rmdir(IntPtr fs, uint dir, byte* name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_rename(IntPtr fs, uint srcDir, byte* srcName, uint dstDir, byte* dstName, int replace);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_set_attr(IntPtr fs, uint ino, uint mask, uint mode, uint uid, uint gid, Times* times);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_enum_disks(delegate* unmanaged[Cdecl]<IntPtr, Part*, int> cb, IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    public static extern int e4_enum_image(string path, delegate* unmanaged[Cdecl]<IntPtr, Part*, int> cb, IntPtr ctx);

    public const uint RawWrite = 0x1;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    public static extern int e4_raw_open(string path, ulong offset, ulong length, uint flags, out IntPtr raw);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_raw_size(IntPtr raw, out ulong size);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_raw_read(IntPtr raw, ulong pos, void* buf, uint len);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_raw_write(IntPtr raw, ulong pos, void* buf, uint len);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_raw_flush(IntPtr raw);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int e4_raw_close(IntPtr raw);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    public static extern int e4_volume_disks(string volume, int* disks, int max);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr e4_strerror(int code);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr e4_version();

    public static string Utf8(byte* p, int max)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return System.Text.Encoding.UTF8.GetString(p, n);
    }

    public static string Wide(char* p, int max)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return new string(p, 0, n);
    }
}
