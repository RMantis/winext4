/*
 * ext4core - small, flat C API over libext2fs for the WinExt4 GUI.
 *
 * All functions are serialised internally per filesystem handle, so callers
 * may use one handle from several threads.  Paths and names are UTF-8.
 * Return values: 0 on success, otherwise an error code that can be turned
 * into text with e4_strerror().  Codes below E4_ERRNO_MAX are POSIX errno
 * values (ENOENT, EEXIST, ...); anything else is a libext2fs errcode_t.
 */
#ifndef EXT4CORE_H
#define EXT4CORE_H

#include <stdint.h>
#include <wchar.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(E4_STATIC)		/* sources compiled into another program (e4fsck) */
#define E4_API
#elif defined(EXT4CORE_BUILD)
#define E4_API __declspec(dllexport)
#else
#define E4_API __declspec(dllimport)
#endif

#define E4_ERRNO_MAX 1000

/* Our own error codes, kept in the errno range. */
#define E4_EVOLUME_LOCK 900 /* volume in use, cannot lock it for writing */
#define E4_ENOTEXT 901      /* no ext2/3/4 superblock found */
#define E4_EUNSUPP_RW 902   /* filesystem has features we refuse to write */
#define E4_ENEEDS_FSCK 903  /* filesystem marked with errors */
#define E4_EDRAFT_MISMATCH 904 /* the draft was made from a different/changed source */
#define E4_EDRAFT_BAD 905      /* not a draft file, or damaged header */
#define E4_EDRAFT_APPLIED 906  /* the draft has already been (or is being) applied */

typedef struct e4_fs e4_fs;

/* e4_open flags */
#define E4_OPEN_RW 0x1          /* open for writing (locks the volume) */
#define E4_OPEN_NO_RECOVERY 0x2 /* RW: don't replay a dirty journal (fails instead) */
#define E4_OPEN_DRAFT_VIEW 0x4  /* e4_open_draft: filesystem read-only (export, compare) */

typedef struct e4_info {
	char label[20];
	char uuid[40];
	char last_mounted[68];
	uint32_t block_size;
	uint32_t inode_size;
	uint64_t blocks_count;
	uint64_t free_blocks;
	uint64_t reserved_blocks;
	uint32_t inodes_count;
	uint32_t free_inodes;
	uint32_t feature_compat;
	uint32_t feature_incompat;
	uint32_t feature_ro_compat;
	uint32_t state;           /* s_state: 1 = clean, 2 = errors */
	int32_t read_write;       /* opened RW */
	int32_t needs_recovery;   /* journal has unreplayed transactions */
	int32_t journal_replayed; /* we replayed the journal while opening RW */
	int32_t has_journal;
	int64_t mount_time;       /* unix seconds */
	int64_t write_time;
	char rw_blocker[160];     /* non-empty: why RW is refused */
	char warnings[256];       /* human readable notes (unchecked fs, ...) */
	int32_t draft;            /* opened as a draft (overlay) */
	uint32_t reserved2;
	uint64_t draft_chunks;    /* 4 KB chunks changed in the draft */
} e4_info;

typedef struct e4_stat {
	uint32_t ino;
	uint32_t mode;  /* Linux st_mode, including file type bits */
	uint32_t uid;
	uint32_t gid;
	uint32_t links;
	uint32_t flags; /* ext4 i_flags */
	uint64_t size;
	uint64_t alloc_size; /* bytes actually allocated */
	int64_t atime_sec, mtime_sec, ctime_sec, crtime_sec; /* crtime 0 = n/a */
	uint32_t atime_nsec, mtime_nsec, ctime_nsec, crtime_nsec;
	uint32_t generation;
	uint32_t reserved;
} e4_stat;

/* Linux file type bits (st_mode & 0170000) */
#define E4_S_IFMT 0170000
#define E4_S_IFSOCK 0140000
#define E4_S_IFLNK 0120000
#define E4_S_IFREG 0100000
#define E4_S_IFBLK 0060000
#define E4_S_IFDIR 0040000
#define E4_S_IFCHR 0020000
#define E4_S_IFIFO 0010000

/* e4_set_attr mask */
#define E4_SET_MODE 0x01   /* permission bits only (07777) */
#define E4_SET_UID 0x02
#define E4_SET_GID 0x04
#define E4_SET_ATIME 0x08
#define E4_SET_MTIME 0x10
#define E4_SET_CTIME 0x20
#define E4_SET_CRTIME 0x40

typedef struct e4_times {
	int64_t atime_sec, mtime_sec, ctime_sec, crtime_sec;
	uint32_t atime_nsec, mtime_nsec, ctime_nsec, crtime_nsec;
} e4_times;

/* Return non-zero to stop the iteration. */
typedef int (__cdecl *e4_dir_cb)(void *ctx, const char *name, int name_len,
				 uint32_t ino, int file_type);

/* Read callback for virtual devices (e.g. an image inside an archive): 0 = ok. */
typedef int (__cdecl *e4_read_cb)(void *ctx, uint64_t pos, void *buf, uint32_t len);

/* ---- filesystem lifetime ---- */
E4_API int e4_open(const wchar_t *path, uint64_t offset, uint64_t length,
		   uint32_t flags, e4_fs **out);
/* Where a filesystem lives: a file/device path, or a read callback. */
typedef struct e4_source {
	const wchar_t *path;	/* NULL when @read is used */
	uint64_t offset;
	uint64_t length;
	e4_read_cb read;
	void *read_ctx;
	uint64_t read_size;
} e4_source;

/*
 * Draft mode: the source is only read; every change goes to the @delta file
 * (created if missing, resumed otherwise). @desc describes the source for
 * the user (stored in the delta). The handle is always read-write.
 */
E4_API int e4_open_draft(const e4_source *src, const wchar_t *delta, const wchar_t *desc,
			 uint32_t flags, e4_fs **out);

typedef struct e4_draft_info {
	uint64_t source_length;
	int64_t created;
	uint64_t changed_chunks;
	uint64_t file_bytes;
	uint32_t chunk_size;
	uint32_t apply_state;	/* 0 none, 1 interrupted, 2 applied */
	char label[20];
	char uuid[40];
	wchar_t source[260];
} e4_draft_info;

E4_API int e4_draft_get_info(const wchar_t *delta, e4_draft_info *out);
/* 0 if @delta belongs to @src (same, unchanged source). */
E4_API int e4_draft_check(const wchar_t *delta, const e4_source *src);
/* Writes the draft into the source (locked for writing). Can be re-run if interrupted. */
E4_API int e4_draft_apply(const wchar_t *delta, const e4_source *src,
			  volatile int32_t *cancel, volatile uint64_t *progress);

/* Raw device content as the filesystem sees it (with the draft, if any). */
E4_API int e4_dev_size(e4_fs *fs, uint64_t *size);
E4_API int e4_dev_read(e4_fs *fs, uint64_t pos, void *buf, uint32_t len);

/* Read-only filesystem on a virtual device of @size bytes, at @offset/@length. */
E4_API int e4_open_cb(e4_read_cb read, void *read_ctx, uint64_t size,
		      uint64_t offset, uint64_t length, uint32_t flags, e4_fs **out);
E4_API int e4_close(e4_fs *fs);
E4_API int e4_get_info(e4_fs *fs, e4_info *info);
E4_API int e4_flush(e4_fs *fs);

/* Everything else of interest in the superblock (dumpe2fs -h style). */
typedef struct e4_super {
	int64_t mkfs_time, last_check, first_error_time, last_error_time;
	uint32_t mnt_count;
	int32_t max_mnt_count;      /* -1 / 0 = no limit */
	uint32_t check_interval;    /* seconds, 0 = none */
	uint32_t errors_behavior;   /* 1 continue, 2 remount-ro, 3 panic */
	uint32_t default_mount_opts;
	uint32_t reserved_uid, reserved_gid;
	uint32_t creator_os, rev_level, minor_rev;
	uint32_t blocks_per_group, inodes_per_group, group_count;
	uint32_t journal_inum;
	uint64_t journal_size;
	uint32_t dir_count;         /* from the group descriptors */
	uint32_t error_count;
	uint32_t first_error_ino, last_error_ino;
	uint32_t first_error_line, last_error_line;
	uint64_t first_error_block, last_error_block;
	char first_error_func[36], last_error_func[36];
	uint64_t kbytes_written;
	uint32_t log_groups_per_flex;
	uint32_t flags;
	char mount_opts[68];
	char hash_seed[40];
	uint32_t has_csum_seed;
	uint32_t uuid_change;       /* 0 ok, 1 needs csum_seed, 2 impossible (stable_inodes) */
} e4_super;

E4_API int e4_get_super(e4_fs *fs, e4_super *out);

/* e4_set_super fields (tune2fs equivalents in brackets) */
#define E4_SUPER_LABEL 1           /* text [e2label] */
#define E4_SUPER_UUID 2            /* text: UUID or "random" [-U] */
#define E4_SUPER_RESERVED_BLOCKS 3 /* value [-r] */
#define E4_SUPER_RESERVED_UID 4    /* value [-u] */
#define E4_SUPER_RESERVED_GID 5    /* value [-g] */
#define E4_SUPER_MAX_MNT_COUNT 6   /* value as int32, -1 = off [-c] */
#define E4_SUPER_CHECK_INTERVAL 7  /* seconds [-i] */
#define E4_SUPER_MNT_COUNT 8       /* value [-C] */
#define E4_SUPER_ERRORS 9          /* 1/2/3 [-e] */
#define E4_SUPER_DEFAULT_MOUNT_OPTS 10 /* bitmask [-o] */
#define E4_SUPER_LAST_MOUNTED 11   /* text, "" clears [-M] */
#define E4_SUPER_ENABLE_CSUM_SEED 12 /* [-O metadata_csum_seed] */

/* Needs a read-write handle. */
E4_API int e4_set_super(e4_fs *fs, int field, uint64_t value, const char *text);

typedef struct e4_counts {
	uint64_t regular, directories, symlinks, special;
	uint64_t regular_bytes;
	uint64_t inodes_scanned;
} e4_counts;

/* Fast count by scanning the inode tables; *cancel may be set from another thread. */
E4_API int e4_count_inodes(e4_fs *fs, volatile int32_t *cancel, volatile uint64_t *progress,
			   e4_counts *out);

typedef struct e4_zero_stats {
	uint64_t free_blocks;   /* free blocks examined */
	uint64_t zeroed_blocks; /* blocks that were not zero and got overwritten */
} e4_zero_stats;

/*
 * Writes zeros over every free block that is not already zero (read first,
 * so flash media is not worn needlessly). Needs read-write. *progress counts
 * examined free blocks; *cancel stops between chunks.
 */
E4_API int e4_zero_free(e4_fs *fs, volatile int32_t *cancel, volatile uint64_t *progress,
			e4_zero_stats *out);

/* ---- lookup / metadata ---- */
E4_API int e4_lookup(e4_fs *fs, const char *path, uint32_t *ino);
E4_API int e4_lookup_child(e4_fs *fs, uint32_t dir, const char *name,
			   uint32_t *ino);
/* Resolve symlink @ino found in directory @dir (absolute links are taken
 * relative to the filesystem root).  Non-links resolve to themselves. */
E4_API int e4_follow(e4_fs *fs, uint32_t dir, uint32_t ino, uint32_t *target);
E4_API int e4_stat_ino(e4_fs *fs, uint32_t ino, e4_stat *st);
E4_API int e4_readdir(e4_fs *fs, uint32_t dir, e4_dir_cb cb, void *ctx);
E4_API int e4_readlink(e4_fs *fs, uint32_t ino, char *buf, uint32_t buf_size,
		       uint32_t *len);
E4_API int e4_statfs(e4_fs *fs, uint64_t *total_bytes, uint64_t *free_bytes,
		     uint64_t *avail_bytes);

/* ---- data ---- */
E4_API int e4_read(e4_fs *fs, uint32_t ino, uint64_t offset, void *buf,
		   uint32_t len, uint32_t *got);
E4_API int e4_write(e4_fs *fs, uint32_t ino, uint64_t offset,
		    const void *buf, uint32_t len, uint32_t *written);
E4_API int e4_set_size(e4_fs *fs, uint32_t ino, uint64_t size);

/* ---- namespace changes ---- */
E4_API int e4_create(e4_fs *fs, uint32_t dir, const char *name, uint32_t mode,
		     uint32_t uid, uint32_t gid, uint32_t *ino);
E4_API int e4_mkdir(e4_fs *fs, uint32_t dir, const char *name, uint32_t mode,
		    uint32_t uid, uint32_t gid, uint32_t *ino);
E4_API int e4_symlink(e4_fs *fs, uint32_t dir, const char *name,
		      const char *target, uint32_t uid, uint32_t gid,
		      uint32_t *ino);
E4_API int e4_unlink(e4_fs *fs, uint32_t dir, const char *name);
E4_API int e4_rmdir(e4_fs *fs, uint32_t dir, const char *name);
E4_API int e4_rename(e4_fs *fs, uint32_t src_dir, const char *src_name,
		     uint32_t dst_dir, const char *dst_name, int replace);
E4_API int e4_set_attr(e4_fs *fs, uint32_t ino, uint32_t mask, uint32_t mode,
		       uint32_t uid, uint32_t gid, const e4_times *times);

/* ---- devices and images ---- */
typedef struct e4_part {
	wchar_t device[96];   /* path to pass to e4_open */
	wchar_t lock_path[96]; /* informational: volume device that gets locked */
	uint64_t offset;      /* pass to e4_open */
	uint64_t length;      /* pass to e4_open */
	int32_t disk_number;  /* -1 for image files */
	int32_t partition_number; /* 0 = whole disk / whole image */
	int32_t bus_type;     /* STORAGE_BUS_TYPE (7 = USB), -1 for images */
	int32_t removable;
	int32_t is_ext;       /* ext2/3/4 superblock found */
	int32_t ext_version;  /* 2, 3 or 4 (best guess from features) */
	char label[20];
	char uuid[40];
	char model[128];      /* disk vendor + product */
	uint64_t disk_size;
	/* partition table */
	wchar_t container[96]; /* \\.\PhysicalDriveN or the image path */
	int32_t part_style;   /* 0 = none, 1 = MBR, 2 = GPT */
	int32_t mbr_type;     /* MBR partition type byte */
	char gpt_type[40];    /* type GUID, e.g. 0FC63DAF-... */
	char gpt_id[40];      /* unique partition GUID (PARTUUID) */
	char gpt_name[80];    /* GPT partition name, UTF-8 */
	uint64_t gpt_attributes;
	uint32_t gpt_status;  /* E4_GPT_* flags, for the whole disk */
	char disk_id[40];     /* GPT disk GUID or MBR signature */
	uint32_t sector_size;
	uint32_t reserved;
} e4_part;

/* gpt_status */
#define E4_GPT_PRIMARY_OK 0x01   /* primary header and entry array CRCs valid */
#define E4_GPT_BACKUP_OK 0x02    /* backup header and entry array CRCs valid */
#define E4_GPT_COPIES_MATCH 0x04 /* primary and backup describe the same partitions */
#define E4_GPT_CHECKED 0x80      /* the checks above were performed */

/* e4_edit_partition */
#define E4_EDIT_MBR_TYPE 0x1
#define E4_EDIT_GPT_TYPE 0x2
#define E4_EDIT_GPT_NAME 0x4
#define E4_EDIT_GPT_ATTRIBUTES 0x8

typedef struct e4_part_edit {
	uint32_t mask;
	int32_t mbr_type;
	char gpt_type[40];
	char gpt_name[80];     /* UTF-8, at most 36 UTF-16 units */
	uint64_t gpt_attributes;
} e4_part_edit;

/*
 * Changes type/name/attributes of a partition. @container is the
 * e4_part.container (physical drive: needs administrator rights; Windows
 * rewrites both GPT copies) or an image file (both copies and CRCs are
 * rewritten here). Logical MBR partitions are not supported.
 */
E4_API int e4_edit_partition(const wchar_t *container, int partition_number,
			     const e4_part_edit *edit);

typedef int (__cdecl *e4_part_cb)(void *ctx, const e4_part *part);

/* Physical disks (needs administrator rights). */
E4_API int e4_enum_disks(e4_part_cb cb, void *ctx);
/* Raw image (.img): whole-filesystem image or an MBR/GPT disk image. */
E4_API int e4_enum_image(const wchar_t *path, e4_part_cb cb, void *ctx);
/* Same as e4_enum_image for a virtual device; @name ends up in e4_part.device. */
E4_API int e4_enum_cb(e4_read_cb read, void *read_ctx, uint64_t size,
		      const wchar_t *name, e4_part_cb cb, void *ctx);

/* ---- raw access (hashing, imaging, writing images) ---- */
typedef struct e4_raw e4_raw;

#define E4_RAW_WRITE 0x1 /* volumes are locked; for \\.\PhysicalDriveN all its volumes */

E4_API int e4_raw_open(const wchar_t *path, uint64_t offset, uint64_t length,
		       uint32_t flags, e4_raw **out);
E4_API int e4_raw_size(e4_raw *r, uint64_t *size);
E4_API int e4_raw_read(e4_raw *r, uint64_t pos, void *buf, uint32_t len);
E4_API int e4_raw_write(e4_raw *r, uint64_t pos, const void *buf, uint32_t len);
E4_API int e4_raw_flush(e4_raw *r);
E4_API int e4_raw_close(e4_raw *r);
/* Disk numbers backing a volume (e.g. L"\\\\.\\C:"); -1 if it cannot be opened. */
E4_API int e4_volume_disks(const wchar_t *volume, int *disks, int max);

E4_API const char *e4_strerror(int code);
E4_API const char *e4_version(void);

#ifdef __cplusplus
}
#endif

#endif
