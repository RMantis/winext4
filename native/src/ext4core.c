/*
 * ext4core.c --- flat C API over libext2fs used by the WinExt4 GUI and the
 * WinFsp mount.  The namespace operations follow misc/fuse2fs.c closely,
 * since that is the reference user-space ext4 writer.
 */
#include <windows.h>
#include <errno.h>
#include <stddef.h>
#include <stdio.h>
#include <time.h>

#include "ext2fs/ext2_fs.h"
#include "ext2fs/ext2fs.h"
#include "ext2fs/ext2_err.h"
#include "uuid/uuid.h"
#include "ext4core.h"
#include "winio.h"
#include "internal.h"

/* debugfs/journal.c */
errcode_t ext2fs_check_ext3_journal(ext2_filsys fs);
errcode_t ext2fs_run_ext3_journal(ext2_filsys *fs);

#define E4_VERSION "0.1.0 (e2fsprogs 1.47.4)"

struct e4_fs {
	CRITICAL_SECTION cs;
	ext2_filsys fs;
	w4_dev *dev;
	int rw;
	int journal_replayed;
	int needs_recovery;
	uint32_t next_generation;
	char rw_blocker[160];
	char warnings[256];
};

#define LOCK(f) EnterCriticalSection(&(f)->cs)
#define UNLOCK(f) LeaveCriticalSection(&(f)->cs)

static INIT_ONCE et_once = INIT_ONCE_STATIC_INIT;

static BOOL CALLBACK init_et(PINIT_ONCE once, PVOID param, PVOID *ctx)
{
	(void) once; (void) param; (void) ctx;
	initialize_ext2_error_table();
	return TRUE;
}

/* ------------------------------------------------------------------ */
/* helpers                                                             */
/* ------------------------------------------------------------------ */

static int xlate(errcode_t err)
{
	switch (err) {
	case 0:
		return 0;
	case EXT2_ET_FILE_NOT_FOUND:
		return ENOENT;
	case EXT2_ET_NO_DIRECTORY:
		return ENOTDIR;
	case EXT2_ET_DIR_EXISTS:
	case EXT2_ET_FILE_EXISTS:
		return EEXIST;
	case EXT2_ET_BLOCK_ALLOC_FAIL:
	case EXT2_ET_INODE_ALLOC_FAIL:
	case EXT2_ET_DIR_NO_SPACE:
	case EXT2_ET_EA_NO_SPACE:
		return ENOSPC;
	case EXT2_ET_RO_FILSYS:
	case EXT2_ET_FILE_RO:
		return EROFS;
	case EXT2_ET_NO_MEMORY:
		return ENOMEM;
	case EXT2_ET_SYMLINK_LOOP:
		return ELOOP;
	case EXT2_ET_INVALID_ARGUMENT:
		return EINVAL;
	case EXT2_ET_FILE_TOO_BIG:
		return EFBIG;
	case EXT2_ET_BAD_MAGIC:
		return E4_ENOTEXT;
	default:
		return (int) err;
	}
}

static errcode_t read_inode(ext2_filsys fs, ext2_ino_t ino,
			    struct ext2_inode_large *inode)
{
	memset(inode, 0, sizeof(*inode));
	return ext2fs_read_inode_full(fs, ino, EXT2_INODE(inode),
				      sizeof(*inode));
}

static errcode_t write_inode(ext2_filsys fs, ext2_ino_t ino,
			     struct ext2_inode_large *inode)
{
	return ext2fs_write_inode_full(fs, ino, EXT2_INODE(inode),
				       sizeof(*inode));
}

/* Does @field lie inside the on-disk part of this (large) inode? */
#define FITS(inode, field)						\
	(offsetof(struct ext2_inode_large, field) + sizeof((inode)->field) <= \
	 EXT2_GOOD_OLD_INODE_SIZE + (size_t) (inode)->i_extra_isize)

static void decode_time(__u32 lo, const __u32 *extra, int64_t *sec,
			uint32_t *nsec)
{
	int64_t s = (int32_t) lo;

	*nsec = 0;
	if (extra) {
		if (*extra & EXT4_EPOCH_MASK)
			s += (int64_t) (*extra & EXT4_EPOCH_MASK) << 32;
		*nsec = (*extra & EXT4_NSEC_MASK) >> EXT4_EPOCH_BITS;
	}
	*sec = s;
}

static __u32 encode_extra(int64_t sec, uint32_t nsec)
{
	return (__u32) (((sec - (int32_t) sec) >> 32) & EXT4_EPOCH_MASK) |
	       (nsec << EXT4_EPOCH_BITS);
}

#define GET_XTIME(inode, f, s, ns)					\
	decode_time((inode)->f, FITS(inode, f##_extra) ?		\
		    &(inode)->f##_extra : NULL, s, ns)

#define SET_XTIME(inode, f, s, ns)					\
	do {								\
		(inode)->f = (__u32) (s);				\
		if (FITS(inode, f##_extra))				\
			(inode)->f##_extra = encode_extra(s, ns);	\
	} while (0)

static void get_now(int64_t *sec, uint32_t *nsec)
{
	FILETIME ft;
	ULARGE_INTEGER t;

	GetSystemTimePreciseAsFileTime(&ft);
	t.LowPart = ft.dwLowDateTime;
	t.HighPart = ft.dwHighDateTime;
	t.QuadPart -= 116444736000000000ULL;
	*sec = (int64_t) (t.QuadPart / 10000000);
	*nsec = (uint32_t) (t.QuadPart % 10000000) * 100;
}

static void increment_version(struct ext2_inode_large *inode)
{
	__u64 ver = inode->osd1.linux1.l_i_version;

	if (FITS(inode, i_version_hi))
		ver |= (__u64) inode->i_version_hi << 32;
	ver++;
	inode->osd1.linux1.l_i_version = (__u32) ver;
	if (FITS(inode, i_version_hi))
		inode->i_version_hi = (__u32) (ver >> 32);
}

static void init_times(struct ext2_inode_large *inode)
{
	int64_t s;
	uint32_t ns;

	get_now(&s, &ns);
	SET_XTIME(inode, i_atime, s, ns);
	SET_XTIME(inode, i_ctime, s, ns);
	SET_XTIME(inode, i_mtime, s, ns);
	if (FITS(inode, i_crtime))
		SET_XTIME(inode, i_crtime, s, ns);
	increment_version(inode);
}

static void touch_ctime(struct ext2_inode_large *inode)
{
	int64_t s;
	uint32_t ns;

	get_now(&s, &ns);
	SET_XTIME(inode, i_ctime, s, ns);
	increment_version(inode);
}

static void touch_mtime(struct ext2_inode_large *inode)
{
	int64_t s;
	uint32_t ns;

	get_now(&s, &ns);
	SET_XTIME(inode, i_mtime, s, ns);
	SET_XTIME(inode, i_ctime, s, ns);
	increment_version(inode);
}

static errcode_t update_ctime(ext2_filsys fs, ext2_ino_t ino)
{
	struct ext2_inode_large inode;
	errcode_t err = read_inode(fs, ino, &inode);

	if (err)
		return err;
	touch_ctime(&inode);
	return write_inode(fs, ino, &inode);
}

static errcode_t update_mtime(ext2_filsys fs, ext2_ino_t ino)
{
	struct ext2_inode_large inode;
	errcode_t err = read_inode(fs, ino, &inode);

	if (err)
		return err;
	touch_mtime(&inode);
	return write_inode(fs, ino, &inode);
}

static void set_owner(struct ext2_inode_large *inode, uint32_t uid,
		      uint32_t gid)
{
	inode->i_uid = (__u16) uid;
	ext2fs_set_i_uid_high(*inode, uid >> 16);
	inode->i_gid = (__u16) gid;
	ext2fs_set_i_gid_high(*inode, gid >> 16);
}

static int file_type(unsigned int mode)
{
	if (LINUX_S_ISREG(mode))
		return EXT2_FT_REG_FILE;
	if (LINUX_S_ISDIR(mode))
		return EXT2_FT_DIR;
	if (LINUX_S_ISCHR(mode))
		return EXT2_FT_CHRDEV;
	if (LINUX_S_ISBLK(mode))
		return EXT2_FT_BLKDEV;
	if (LINUX_S_ISLNK(mode))
		return EXT2_FT_SYMLINK;
	if (LINUX_S_ISFIFO(mode))
		return EXT2_FT_FIFO;
	if (LINUX_S_ISSOCK(mode))
		return EXT2_FT_SOCK;
	return 0;
}

/*
 * Like fuse2fs: never hand out the reserved blocks.  libext2fs can corrupt
 * the filesystem if it runs completely out of space mid-operation.
 */
static int can_allocate(ext2_filsys fs, blk64_t num)
{
	blk64_t reserved = ext2fs_r_blocks_count(fs->super);

	if (num > ext2fs_blocks_count(fs->super))
		return 0;
	if (reserved == 0)
		reserved = ext2fs_blocks_count(fs->super) / 10;
	return ext2fs_free_blocks_count(fs->super) > reserved + num;
}

static int check_name(const char *name)
{
	size_t len;

	if (!name)
		return EINVAL;
	len = strlen(name);
	if (len == 0 || strchr(name, '/') || !strcmp(name, ".") ||
	    !strcmp(name, ".."))
		return EINVAL;
	if (len > EXT2_NAME_LEN)
		return ENAMETOOLONG;
	return 0;
}

static errcode_t lookup(ext2_filsys fs, ext2_ino_t dir, const char *name,
			ext2_ino_t *ino)
{
	return ext2fs_lookup(fs, dir, name, (int) strlen(name), NULL, ino);
}

static int writable(e4_fs *f)
{
	return f->rw && (f->fs->flags & EXT2_FLAG_RW);
}

/* for the other API files (internal.h) */
void e4_lock(e4_fs *f) { LOCK(f); }
void e4_unlock(e4_fs *f) { UNLOCK(f); }
ext2_filsys e4_filsys(e4_fs *f) { return f->fs; }
int e4_writable(e4_fs *f) { return writable(f); }
int e4_xlate(errcode_t err) { return xlate(err); }

/* ------------------------------------------------------------------ */
/* open / close                                                        */
/* ------------------------------------------------------------------ */

static void append(char *buf, size_t size, const char *text)
{
	size_t used = strlen(buf);

	if (used && used + 2 < size) {
		strcat(buf, " ");
		used++;
	}
	if (used < size - 1)
		strncat(buf, text, size - used - 1);
}

static void compute_blockers(e4_fs *f, uint32_t flags)
{
	struct ext2_super_block *sb = f->fs->super;
	char *b = f->rw_blocker;
	size_t n = sizeof(f->rw_blocker);

	if (ext2fs_has_feature_quota(sb))
		append(b, n, "quota feature;");
	if (ext2fs_has_feature_verity(sb))
		append(b, n, "verity feature;");
	if (ext2fs_has_feature_encrypt(sb))
		append(b, n, "encryption feature;");
	if (ext2fs_has_feature_casefold(sb))
		append(b, n, "casefold feature;");
	if (ext2fs_has_feature_shared_blocks(sb))
		append(b, n, "shared blocks;");
	if (ext2fs_has_feature_mmp(sb))
		append(b, n, "multi-mount protection;");
	if (ext2fs_has_feature_journal_dev(sb) ||
	    (ext2fs_has_feature_journal(sb) && sb->s_journal_inum == 0))
		append(b, n, "external journal;");
	if (sb->s_state & EXT2_ERROR_FS)
		append(b, n, "filesystem has errors (run e2fsck);");
	if (ext2fs_has_feature_journal_needs_recovery(sb) &&
	    (flags & E4_OPEN_NO_RECOVERY))
		append(b, n, "journal needs recovery;");
}

E4_API int e4_open(const wchar_t *path, uint64_t offset, uint64_t length,
		   uint32_t flags, e4_fs **out)
{
	e4_fs *f;
	char name[64];
	int rw = (flags & E4_OPEN_RW) != 0;
	int oflags, ret;
	errcode_t err;
	struct ext2_super_block *sb;
	uint32_t seed = 0;

	InitOnceExecuteOnce(&et_once, init_et, NULL, NULL);
	if (!path || !out)
		return EINVAL;
	*out = NULL;

	f = calloc(1, sizeof(*f));
	if (!f)
		return ENOMEM;
	InitializeCriticalSection(&f->cs);
	f->rw = rw;

	ret = w4_dev_open(path, offset, length, rw ? W4_DEV_WRITE : 0, &f->dev);
	if (ret)
		goto fail;

	w4_dev_name(f->dev, name, sizeof(name));
	oflags = EXT2_FLAG_64BITS | EXT2_FLAG_THREADS;
	if (rw)
		oflags |= EXT2_FLAG_RW;
	else
		oflags |= EXT2_FLAG_SKIP_MMP;

	err = ext2fs_open2(name, NULL, oflags, 0, 0, w4_io_manager, &f->fs);
	if (err && !rw && (err == EXT2_ET_SB_CSUM_INVALID ||
			   err == EXT2_ET_UNKNOWN_CSUM)) {
		err = ext2fs_open2(name, NULL,
				   oflags | EXT2_FLAG_IGNORE_CSUM_ERRORS, 0, 0,
				   w4_io_manager, &f->fs);
		if (!err)
			append(f->warnings, sizeof(f->warnings),
			       "Checksum errors ignored (read-only).");
	}
	if (err) {
		ret = xlate(err);
		f->fs = NULL;
		goto fail;
	}
	sb = f->fs->super;

	compute_blockers(f, flags);
	f->needs_recovery = ext2fs_has_feature_journal_needs_recovery(sb);

	if (rw && f->rw_blocker[0]) {
		ret = (sb->s_state & EXT2_ERROR_FS) ? E4_ENEEDS_FSCK :
						       E4_EUNSUPP_RW;
		goto fail;
	}

	if (f->needs_recovery) {
		if (rw) {
			err = ext2fs_run_ext3_journal(&f->fs);
			if (err) {
				ret = xlate(err);
				goto fail;
			}
			f->journal_replayed = 1;
			f->needs_recovery = 0;
			sb = f->fs->super;
		} else {
			append(f->warnings, sizeof(f->warnings),
			       "The journal was not replayed: the most recent "
			       "changes may not be visible.");
		}
	} else if (rw && ext2fs_has_feature_journal(sb)) {
		err = ext2fs_check_ext3_journal(f->fs);
		if (err) {
			ret = xlate(err);
			goto fail;
		}
	}

	if (!(sb->s_state & EXT2_VALID_FS))
		append(f->warnings, sizeof(f->warnings),
		       "Filesystem was not cleanly unmounted (e2fsck "
		       "recommended).");
	if (sb->s_last_orphan)
		append(f->warnings, sizeof(f->warnings),
		       "Orphan inodes present (e2fsck recommended).");

	if (rw) {
		err = ext2fs_read_bitmaps(f->fs);
		if (err) {
			ret = xlate(err);
			goto fail;
		}
		/* Mark "not clean" while we own it, as the kernel does. */
		sb->s_mnt_count++;
		ext2fs_set_tstamp(sb, s_mtime, time(NULL));
		sb->s_state &= ~EXT2_VALID_FS;
		ext2fs_mark_super_dirty(f->fs);
		err = ext2fs_flush2(f->fs, 0);
		if (err) {
			ret = xlate(err);
			goto fail;
		}
	}

	{
		LARGE_INTEGER pc;

		QueryPerformanceCounter(&pc);
		seed = (uint32_t) pc.QuadPart ^ (uint32_t) time(NULL);
	}
	f->next_generation = seed;
	*out = f;
	return 0;

fail:
	if (f->fs) {
		/* never write anything back on a failed open */
		f->fs->flags &= ~EXT2_FLAG_DIRTY;
		f->fs->flags &= ~EXT2_FLAG_RW;
		ext2fs_free(f->fs);
	}
	w4_dev_close(f->dev);
	DeleteCriticalSection(&f->cs);
	free(f);
	return ret;
}

E4_API int e4_close(e4_fs *f)
{
	errcode_t err = 0, err2;

	if (!f)
		return EINVAL;
	LOCK(f);
	if (writable(f)) {
		struct ext2_super_block *sb = f->fs->super;

		sb->s_state |= EXT2_VALID_FS;
		if (sb->s_error_count)
			sb->s_state |= EXT2_ERROR_FS;
		ext2fs_mark_super_dirty(f->fs);
		err = ext2fs_set_gdt_csum(f->fs);
		err2 = ext2fs_flush2(f->fs, 0);
		if (!err)
			err = err2;
	}
	err2 = ext2fs_close_free(&f->fs);
	if (!err)
		err = err2;
	w4_dev_close(f->dev);
	UNLOCK(f);
	DeleteCriticalSection(&f->cs);
	free(f);
	return xlate(err);
}

E4_API int e4_flush(e4_fs *f)
{
	errcode_t err = 0;

	LOCK(f);
	if (writable(f))
		err = ext2fs_flush2(f->fs, 0);
	UNLOCK(f);
	return xlate(err);
}

E4_API int e4_get_info(e4_fs *f, e4_info *info)
{
	struct ext2_super_block *sb;

	memset(info, 0, sizeof(*info));
	LOCK(f);
	sb = f->fs->super;
	memcpy(info->label, sb->s_volume_name, sizeof(sb->s_volume_name));
	if (!uuid_is_null(sb->s_uuid))
		uuid_unparse(sb->s_uuid, info->uuid);
	memcpy(info->last_mounted, sb->s_last_mounted,
	       sizeof(sb->s_last_mounted));
	info->block_size = f->fs->blocksize;
	info->inode_size = EXT2_INODE_SIZE(sb);
	info->blocks_count = ext2fs_blocks_count(sb);
	info->free_blocks = ext2fs_free_blocks_count(sb);
	info->reserved_blocks = ext2fs_r_blocks_count(sb);
	info->inodes_count = sb->s_inodes_count;
	info->free_inodes = sb->s_free_inodes_count;
	info->feature_compat = sb->s_feature_compat;
	info->feature_incompat = sb->s_feature_incompat;
	info->feature_ro_compat = sb->s_feature_ro_compat;
	info->state = sb->s_state;
	info->read_write = writable(f);
	info->needs_recovery = f->needs_recovery;
	info->journal_replayed = f->journal_replayed;
	info->has_journal = ext2fs_has_feature_journal(sb);
	info->mount_time = ext2fs_get_tstamp(sb, s_mtime);
	info->write_time = ext2fs_get_tstamp(sb, s_wtime);
	strncpy(info->rw_blocker, f->rw_blocker, sizeof(info->rw_blocker) - 1);
	strncpy(info->warnings, f->warnings, sizeof(info->warnings) - 1);
	UNLOCK(f);
	return 0;
}

E4_API int e4_statfs(e4_fs *f, uint64_t *total, uint64_t *free_bytes,
		     uint64_t *avail)
{
	struct ext2_super_block *sb;
	uint64_t bs, fr, res;

	LOCK(f);
	sb = f->fs->super;
	bs = f->fs->blocksize;
	fr = ext2fs_free_blocks_count(sb);
	res = ext2fs_r_blocks_count(sb);
	*total = ext2fs_blocks_count(sb) * bs;
	*free_bytes = fr * bs;
	*avail = (fr > res ? fr - res : 0) * bs;
	UNLOCK(f);
	return 0;
}

/* ------------------------------------------------------------------ */
/* lookup / metadata                                                   */
/* ------------------------------------------------------------------ */

E4_API int e4_lookup(e4_fs *f, const char *path, uint32_t *ino)
{
	ext2_ino_t i = 0;
	errcode_t err;

	LOCK(f);
	err = ext2fs_namei(f->fs, EXT2_ROOT_INO, EXT2_ROOT_INO, path, &i);
	UNLOCK(f);
	*ino = i;
	return xlate(err);
}

E4_API int e4_lookup_child(e4_fs *f, uint32_t dir, const char *name,
			   uint32_t *ino)
{
	ext2_ino_t i = 0;
	errcode_t err;

	LOCK(f);
	err = lookup(f->fs, dir, name, &i);
	UNLOCK(f);
	*ino = i;
	return xlate(err);
}

E4_API int e4_follow(e4_fs *f, uint32_t dir, uint32_t ino, uint32_t *target)
{
	ext2_ino_t res = 0;
	errcode_t err;

	LOCK(f);
	err = ext2fs_follow_link(f->fs, EXT2_ROOT_INO, dir, ino, &res);
	UNLOCK(f);
	*target = res;
	return xlate(err);
}

static void fill_stat(ext2_filsys fs, ext2_ino_t ino,
		      struct ext2_inode_large *inode, e4_stat *st)
{
	memset(st, 0, sizeof(*st));
	st->ino = ino;
	st->mode = inode->i_mode;
	st->uid = inode_uid(*inode);
	st->gid = inode_gid(*inode);
	st->links = inode->i_links_count;
	st->flags = inode->i_flags;
	st->size = EXT2_I_SIZE(inode);
	st->alloc_size = ext2fs_get_stat_i_blocks(fs, EXT2_INODE(inode)) * 512;
	st->generation = inode->i_generation;
	GET_XTIME(inode, i_atime, &st->atime_sec, &st->atime_nsec);
	GET_XTIME(inode, i_mtime, &st->mtime_sec, &st->mtime_nsec);
	GET_XTIME(inode, i_ctime, &st->ctime_sec, &st->ctime_nsec);
	if (FITS(inode, i_crtime))
		GET_XTIME(inode, i_crtime, &st->crtime_sec, &st->crtime_nsec);
}

E4_API int e4_stat_ino(e4_fs *f, uint32_t ino, e4_stat *st)
{
	struct ext2_inode_large inode;
	errcode_t err;

	LOCK(f);
	err = read_inode(f->fs, ino, &inode);
	if (!err)
		fill_stat(f->fs, ino, &inode, st);
	UNLOCK(f);
	return xlate(err);
}

struct rd_ctx {
	e4_dir_cb cb;
	void *ctx;
};

static int readdir_proc(ext2_ino_t dir, int entry,
			struct ext2_dir_entry *dirent, int offset,
			int blocksize, char *buf, void *priv)
{
	struct rd_ctx *rc = priv;

	(void) dir; (void) entry; (void) offset; (void) blocksize; (void) buf;
	if (dirent->inode == 0)
		return 0;
	if (rc->cb(rc->ctx, dirent->name, ext2fs_dirent_name_len(dirent),
		   dirent->inode, ext2fs_dirent_file_type(dirent)))
		return DIRENT_ABORT;
	return 0;
}

E4_API int e4_readdir(e4_fs *f, uint32_t dir, e4_dir_cb cb, void *ctx)
{
	struct rd_ctx rc = { cb, ctx };
	errcode_t err;

	LOCK(f);
	err = ext2fs_dir_iterate2(f->fs, dir, 0, NULL, readdir_proc, &rc);
	UNLOCK(f);
	return xlate(err);
}

E4_API int e4_readlink(e4_fs *f, uint32_t ino, char *buf, uint32_t buf_size,
		       uint32_t *len)
{
	struct ext2_inode_large inode;
	ext2_file_t file;
	unsigned int got = 0;
	size_t size;
	errcode_t err;
	int ret = 0;

	*len = 0;
	LOCK(f);
	err = read_inode(f->fs, ino, &inode);
	if (err) {
		ret = xlate(err);
		goto out;
	}
	if (!LINUX_S_ISLNK(inode.i_mode)) {
		ret = EINVAL;
		goto out;
	}
	size = (size_t) EXT2_I_SIZE(&inode);
	if (size >= buf_size) {
		ret = ENAMETOOLONG;
		goto out;
	}

	if (ext2fs_is_fast_symlink(EXT2_INODE(&inode))) {
		memcpy(buf, (char *) inode.i_block, size);
		got = (unsigned int) size;
	} else if (inode.i_flags & EXT4_INLINE_DATA_FL) {
		size_t isz = buf_size;

		err = ext2fs_inline_data_get(f->fs, ino, EXT2_INODE(&inode),
					     buf, &isz);
		if (err) {
			ret = xlate(err);
			goto out;
		}
		got = (unsigned int) (isz < size ? isz : size);
	} else {
		err = ext2fs_file_open(f->fs, ino, 0, &file);
		if (err) {
			ret = xlate(err);
			goto out;
		}
		err = ext2fs_file_read(file, buf, (unsigned int) size, &got);
		ext2fs_file_close(file);
		if (err) {
			ret = xlate(err);
			goto out;
		}
	}
	buf[got] = 0;
	*len = got;
out:
	UNLOCK(f);
	return ret;
}

/* ------------------------------------------------------------------ */
/* data                                                                */
/* ------------------------------------------------------------------ */

E4_API int e4_read(e4_fs *f, uint32_t ino, uint64_t offset, void *buf,
		   uint32_t len, uint32_t *got)
{
	ext2_file_t file;
	unsigned int n = 0;
	errcode_t err, err2;

	*got = 0;
	LOCK(f);
	err = ext2fs_file_open(f->fs, ino, 0, &file);
	if (!err) {
		__u64 size = 0;

		err = ext2fs_file_llseek(file, offset, EXT2_SEEK_SET, NULL);
		if (!err)
			err = ext2fs_file_read(file, buf, len, &n);
		/*
		 * For inline-data files libext2fs zero-fills the whole buffer and
		 * counts it as read; never report bytes past EOF.
		 */
		if (!err && !ext2fs_file_get_lsize(file, &size))
			n = offset >= size ? 0 :
			    (unsigned int) (size - offset < n ? size - offset : n);
		err2 = ext2fs_file_close(file);
		if (!err)
			err = err2;
	}
	UNLOCK(f);
	*got = n;
	return xlate(err);
}

E4_API int e4_write(e4_fs *f, uint32_t ino, uint64_t offset,
		    const void *buf, uint32_t len, uint32_t *written)
{
	ext2_file_t file;
	unsigned int n = 0;
	errcode_t err, err2;
	int ret = 0;

	*written = 0;
	LOCK(f);
	if (!writable(f)) {
		ret = EROFS;
		goto out;
	}
	if (!can_allocate(f->fs, (len + f->fs->blocksize - 1) /
				 f->fs->blocksize)) {
		ret = ENOSPC;
		goto out;
	}
	err = ext2fs_file_open(f->fs, ino, EXT2_FILE_WRITE, &file);
	if (err) {
		ret = xlate(err);
		goto out;
	}
	err = ext2fs_file_llseek(file, offset, EXT2_SEEK_SET, NULL);
	if (!err)
		err = ext2fs_file_write(file, buf, len, &n);
	if (!err)
		err = ext2fs_file_flush(file);
	err2 = ext2fs_file_close(file);
	if (!err)
		err = err2;
	if (err) {
		ret = xlate(err);
		goto out;
	}
	*written = n;
	ret = xlate(update_mtime(f->fs, ino));
out:
	UNLOCK(f);
	return ret;
}

E4_API int e4_set_size(e4_fs *f, uint32_t ino, uint64_t size)
{
	struct ext2_inode_large inode;
	ext2_file_t file;
	__u64 old_size = 0;
	errcode_t err, err2;
	int ret = 0;

	LOCK(f);
	if (!writable(f)) {
		ret = EROFS;
		goto out;
	}
	err = ext2fs_file_open(f->fs, ino, EXT2_FILE_WRITE, &file);
	if (err) {
		ret = xlate(err);
		goto out;
	}
	err = ext2fs_file_get_lsize(file, &old_size);
	if (!err && size > old_size &&
	    !can_allocate(f->fs, 1))
		err = EXT2_ET_BLOCK_ALLOC_FAIL;
	if (!err)
		err = ext2fs_file_set_size2(file, size);
	err2 = ext2fs_file_close(file);
	if (!err)
		err = err2;
	if (err) {
		ret = xlate(err);
		goto out;
	}
	err = update_mtime(f->fs, ino);
	if (!err && size == old_size) {
		/* Truncating to the same size drops post-EOF preallocations. */
		err = read_inode(f->fs, ino, &inode);
		if (!err)
			err = ext2fs_punch(f->fs, ino, EXT2_INODE(&inode), NULL,
					   (size + f->fs->blocksize - 1) /
					   f->fs->blocksize, ~0ULL);
		if (!err)
			err = write_inode(f->fs, ino, &inode);
	}
	ret = xlate(err);
out:
	UNLOCK(f);
	return ret;
}

/* ------------------------------------------------------------------ */
/* namespace                                                           */
/* ------------------------------------------------------------------ */

static int precheck_new_entry(e4_fs *f, uint32_t dir, const char *name)
{
	struct ext2_inode_large dinode;
	ext2_ino_t existing;
	errcode_t err;
	int ret;

	if (!writable(f))
		return EROFS;
	ret = check_name(name);
	if (ret)
		return ret;
	err = read_inode(f->fs, dir, &dinode);
	if (err)
		return xlate(err);
	if (!LINUX_S_ISDIR(dinode.i_mode))
		return ENOTDIR;
	err = lookup(f->fs, dir, name, &existing);
	if (!err)
		return EEXIST;
	if (err != EXT2_ET_FILE_NOT_FOUND)
		return xlate(err);
	if (!can_allocate(f->fs, 1))
		return ENOSPC;
	return 0;
}

E4_API int e4_create(e4_fs *f, uint32_t dir, const char *name, uint32_t mode,
		     uint32_t uid, uint32_t gid, uint32_t *ino)
{
	struct ext2_inode_large inode;
	ext2_ino_t child;
	errcode_t err;
	int ret;

	*ino = 0;
	LOCK(f);
	ret = precheck_new_entry(f, dir, name);
	if (ret)
		goto out;

	mode = LINUX_S_IFREG | (mode & 07777);
	err = ext2fs_new_inode(f->fs, dir, mode, 0, &child);
	if (err)
		goto fail;
	err = ext2fs_link(f->fs, dir, name, child,
			  EXT2_FT_REG_FILE | EXT2FS_LINK_EXPAND);
	if (err)
		goto fail;
	err = update_mtime(f->fs, dir);
	if (err)
		goto fail;

	memset(&inode, 0, sizeof(inode));
	inode.i_mode = (__u16) mode;
	inode.i_links_count = 1;
	if (EXT2_INODE_SIZE(f->fs->super) > EXT2_GOOD_OLD_INODE_SIZE)
		inode.i_extra_isize = sizeof(struct ext2_inode_large) -
				      EXT2_GOOD_OLD_INODE_SIZE;
	set_owner(&inode, uid, gid);
	if (ext2fs_has_feature_extents(f->fs->super)) {
		ext2_extent_handle_t handle;

		inode.i_flags &= ~EXT4_EXTENTS_FL;
		err = ext2fs_extent_open2(f->fs, child, EXT2_INODE(&inode),
					  &handle);
		if (err)
			goto fail;
		ext2fs_extent_free(handle);
	}
	err = ext2fs_write_new_inode(f->fs, child, EXT2_INODE(&inode));
	if (err)
		goto fail;
	inode.i_generation = f->next_generation++;
	init_times(&inode);
	err = write_inode(f->fs, child, &inode);
	if (err)
		goto fail;
	ext2fs_inode_alloc_stats2(f->fs, child, 1, 0);
	*ino = child;
	goto out;
fail:
	ret = xlate(err);
out:
	UNLOCK(f);
	return ret;
}

E4_API int e4_mkdir(e4_fs *f, uint32_t dir, const char *name, uint32_t mode,
		    uint32_t uid, uint32_t gid, uint32_t *ino)
{
	struct ext2_inode_large inode;
	ext2_ino_t child = 0;
	char *block;
	blk64_t blk;
	errcode_t err;
	int ret;

	*ino = 0;
	LOCK(f);
	ret = precheck_new_entry(f, dir, name);
	if (ret)
		goto out;

	err = ext2fs_mkdir2(f->fs, dir, 0, 0, EXT2FS_LINK_EXPAND, name, &child);
	if (err)
		goto fail;
	err = update_mtime(f->fs, dir);
	if (err)
		goto fail;

	err = read_inode(f->fs, child, &inode);
	if (err)
		goto fail;
	set_owner(&inode, uid, gid);
	inode.i_mode = (__u16) (LINUX_S_IFDIR | (mode & 07777 & ~LINUX_S_ISUID));
	inode.i_generation = f->next_generation++;
	init_times(&inode);
	err = write_inode(f->fs, child, &inode);
	if (err)
		goto fail;

	/* Rewrite the directory block checksum, having set i_generation */
	if (!(inode.i_flags & EXT4_INLINE_DATA_FL) &&
	    ext2fs_has_feature_metadata_csum(f->fs->super)) {
		err = ext2fs_new_dir_block(f->fs, child, dir, &block);
		if (err)
			goto fail;
		err = ext2fs_bmap2(f->fs, child, EXT2_INODE(&inode), NULL, 0, 0,
				   NULL, &blk);
		if (!err)
			err = ext2fs_write_dir_block4(f->fs, blk, block, 0,
						      child);
		ext2fs_free_mem(&block);
		if (err)
			goto fail;
	}
	*ino = child;
	goto out;
fail:
	ret = xlate(err);
out:
	UNLOCK(f);
	return ret;
}

E4_API int e4_symlink(e4_fs *f, uint32_t dir, const char *name,
		      const char *target, uint32_t uid, uint32_t gid,
		      uint32_t *ino)
{
	struct ext2_inode_large inode;
	ext2_ino_t child;
	errcode_t err;
	int ret;

	*ino = 0;
	LOCK(f);
	ret = precheck_new_entry(f, dir, name);
	if (ret)
		goto out;
	if (!target || !*target || strlen(target) >= f->fs->blocksize) {
		ret = ENAMETOOLONG;
		goto out;
	}

	err = ext2fs_symlink(f->fs, dir, 0, name, target);
	if (err == EXT2_ET_DIR_NO_SPACE) {
		err = ext2fs_expand_dir(f->fs, dir);
		if (!err)
			err = ext2fs_symlink(f->fs, dir, 0, name, target);
	}
	if (err)
		goto fail;
	err = update_mtime(f->fs, dir);
	if (err)
		goto fail;
	err = lookup(f->fs, dir, name, &child);
	if (err)
		goto fail;
	err = read_inode(f->fs, child, &inode);
	if (err)
		goto fail;
	set_owner(&inode, uid, gid);
	inode.i_generation = f->next_generation++;
	init_times(&inode);
	err = write_inode(f->fs, child, &inode);
	if (err)
		goto fail;
	*ino = child;
	goto out;
fail:
	ret = xlate(err);
out:
	UNLOCK(f);
	return ret;
}

static errcode_t remove_ea_inodes(ext2_filsys fs, ext2_ino_t ino,
				  struct ext2_inode_large *inode)
{
	struct ext2_xattr_handle *h;
	errcode_t err;

	/* The xattr handle reads the inode itself, so write ours first. */
	err = write_inode(fs, ino, inode);
	if (err)
		return err;
	err = ext2fs_xattrs_open(fs, ino, &h);
	if (err)
		return err;
	err = ext2fs_xattrs_read(h);
	if (!err)
		err = ext2fs_xattr_remove_all(h);
	ext2fs_xattrs_close(&h);
	if (err)
		return err;
	return read_inode(fs, ino, inode);
}

/* Drop one link to @ino, freeing it when the count reaches zero. */
static errcode_t remove_inode(ext2_filsys fs, ext2_ino_t ino)
{
	struct ext2_inode_large inode;
	errcode_t err;

	err = read_inode(fs, ino, &inode);
	if (err)
		return err;

	switch (inode.i_links_count) {
	case 0:
		return 0;
	case 1:
		inode.i_links_count--;
		ext2fs_set_dtime(fs, EXT2_INODE(&inode));
		break;
	default:
		inode.i_links_count--;
	}
	touch_ctime(&inode);
	if (inode.i_links_count)
		return write_inode(fs, ino, &inode);

	if (ext2fs_has_feature_ea_inode(fs->super)) {
		err = remove_ea_inodes(fs, ino, &inode);
		if (err)
			return err;
	}
	err = ext2fs_free_ext_attr(fs, ino, &inode);
	if (err)
		return err;
	if (ext2fs_inode_has_valid_blocks2(fs, EXT2_INODE(&inode))) {
		err = ext2fs_punch(fs, ino, EXT2_INODE(&inode), NULL, 0, ~0ULL);
		if (err)
			return err;
	}
	ext2fs_inode_alloc_stats2(fs, ino, -1, LINUX_S_ISDIR(inode.i_mode));
	return write_inode(fs, ino, &inode);
}

static int do_unlink(e4_fs *f, ext2_ino_t dir, const char *name)
{
	struct ext2_inode_large inode;
	ext2_ino_t ino;
	errcode_t err;

	err = lookup(f->fs, dir, name, &ino);
	if (err)
		return xlate(err);
	err = read_inode(f->fs, ino, &inode);
	if (err)
		return xlate(err);
	if (LINUX_S_ISDIR(inode.i_mode))
		return EISDIR;
	err = ext2fs_unlink(f->fs, dir, name, 0, 0);
	if (!err)
		err = update_mtime(f->fs, dir);
	if (!err)
		err = remove_inode(f->fs, ino);
	return xlate(err);
}

struct rd_struct {
	ext2_ino_t parent;
	int empty;
};

static int rmdir_proc(ext2_ino_t dir, int entry, struct ext2_dir_entry *dirent,
		      int offset, int blocksize, char *buf, void *priv)
{
	struct rd_struct *rds = priv;
	int len = ext2fs_dirent_name_len(dirent);

	(void) dir; (void) entry; (void) offset; (void) blocksize; (void) buf;
	if (dirent->inode == 0)
		return 0;
	if (len == 1 && dirent->name[0] == '.')
		return 0;
	if (len == 2 && dirent->name[0] == '.' && dirent->name[1] == '.') {
		rds->parent = dirent->inode;
		return 0;
	}
	rds->empty = 0;
	return 0;
}

static int do_rmdir(e4_fs *f, ext2_ino_t dir, const char *name)
{
	struct ext2_inode_large inode;
	struct rd_struct rds;
	ext2_ino_t child;
	errcode_t err;

	err = lookup(f->fs, dir, name, &child);
	if (err)
		return xlate(err);
	if (child == EXT2_ROOT_INO)
		return EBUSY;
	err = read_inode(f->fs, child, &inode);
	if (err)
		return xlate(err);
	if (!LINUX_S_ISDIR(inode.i_mode))
		return ENOTDIR;

	rds.parent = 0;
	rds.empty = 1;
	err = ext2fs_dir_iterate2(f->fs, child, 0, NULL, rmdir_proc, &rds);
	if (err)
		return xlate(err);
	if (rds.parent == 0)
		return xlate(EXT2_ET_FILESYSTEM_CORRUPTED);
	if (!rds.empty)
		return ENOTEMPTY;

	err = ext2fs_unlink(f->fs, dir, name, 0, 0);
	if (!err)
		err = update_mtime(f->fs, dir);
	/* Directories have to be "removed" twice ("." and the name). */
	if (!err)
		err = remove_inode(f->fs, child);
	if (!err)
		err = remove_inode(f->fs, child);
	if (err)
		return xlate(err);

	err = read_inode(f->fs, rds.parent, &inode);
	if (err)
		return xlate(err);
	if (inode.i_links_count > 1)
		inode.i_links_count--;
	touch_mtime(&inode);
	return xlate(write_inode(f->fs, rds.parent, &inode));
}

E4_API int e4_unlink(e4_fs *f, uint32_t dir, const char *name)
{
	int ret;

	LOCK(f);
	ret = writable(f) ? check_name(name) : EROFS;
	if (!ret)
		ret = do_unlink(f, dir, name);
	UNLOCK(f);
	return ret;
}

E4_API int e4_rmdir(e4_fs *f, uint32_t dir, const char *name)
{
	int ret;

	LOCK(f);
	ret = writable(f) ? check_name(name) : EROFS;
	if (!ret)
		ret = do_rmdir(f, dir, name);
	UNLOCK(f);
	return ret;
}

struct update_dotdot {
	ext2_ino_t new_dotdot;
};

static int update_dotdot_proc(ext2_ino_t dir, int entry,
			      struct ext2_dir_entry *dirent, int offset,
			      int blocksize, char *buf, void *priv)
{
	struct update_dotdot *ud = priv;

	(void) dir; (void) entry; (void) offset; (void) blocksize; (void) buf;
	if (ext2fs_dirent_name_len(dirent) == 2 &&
	    dirent->name[0] == '.' && dirent->name[1] == '.') {
		dirent->inode = ud->new_dotdot;
		return DIRENT_CHANGED | DIRENT_ABORT;
	}
	return 0;
}

/* Is @dir equal to, or below, @ancestor? */
static int is_within(ext2_filsys fs, ext2_ino_t dir, ext2_ino_t ancestor)
{
	ext2_ino_t cur = dir, parent;
	int depth = 0;

	while (depth++ < 4096) {
		if (cur == ancestor)
			return 1;
		if (cur == EXT2_ROOT_INO)
			return 0;
		if (lookup(fs, cur, "..", &parent) || parent == cur)
			return 0;
		cur = parent;
	}
	return 1; /* loop: be conservative */
}

E4_API int e4_rename(e4_fs *f, uint32_t src_dir, const char *src_name,
		     uint32_t dst_dir, const char *dst_name, int replace)
{
	struct ext2_inode_large from, to, inode;
	struct update_dotdot ud;
	ext2_ino_t from_ino, to_ino = 0;
	errcode_t err;
	int ret = 0, from_is_dir;

	LOCK(f);
	if (!writable(f)) {
		ret = EROFS;
		goto out;
	}
	ret = check_name(src_name);
	if (!ret)
		ret = check_name(dst_name);
	if (ret)
		goto out;
	if (!can_allocate(f->fs, 5)) {
		ret = ENOSPC;
		goto out;
	}

	err = lookup(f->fs, src_dir, src_name, &from_ino);
	if (err)
		goto fail;
	err = read_inode(f->fs, from_ino, &from);
	if (err)
		goto fail;
	from_is_dir = LINUX_S_ISDIR(from.i_mode);

	err = read_inode(f->fs, dst_dir, &inode);
	if (err)
		goto fail;
	if (!LINUX_S_ISDIR(inode.i_mode)) {
		ret = ENOTDIR;
		goto out;
	}

	err = lookup(f->fs, dst_dir, dst_name, &to_ino);
	if (err && err != EXT2_ET_FILE_NOT_FOUND)
		goto fail;
	if (err)
		to_ino = 0;

	if (src_dir == dst_dir && !strcmp(src_name, dst_name))
		goto out;
	if (to_ino && to_ino == from_ino)
		goto out; /* two names of the same file: nothing to do */

	if (from_is_dir && is_within(f->fs, dst_dir, from_ino)) {
		ret = EINVAL;
		goto out;
	}

	if (to_ino) {
		if (!replace) {
			ret = EEXIST;
			goto out;
		}
		err = read_inode(f->fs, to_ino, &to);
		if (err)
			goto fail;
		if (LINUX_S_ISDIR(to.i_mode)) {
			if (!from_is_dir) {
				ret = EISDIR;
				goto out;
			}
			ret = do_rmdir(f, dst_dir, dst_name);
		} else {
			if (from_is_dir) {
				ret = ENOTDIR;
				goto out;
			}
			ret = do_unlink(f, dst_dir, dst_name);
		}
		if (ret)
			goto out;
	}

	/* Link in the new name */
	err = ext2fs_link(f->fs, dst_dir, dst_name, from_ino,
			  file_type(from.i_mode) | EXT2FS_LINK_EXPAND);
	if (err)
		goto fail;

	if (from_is_dir && src_dir != dst_dir) {
		ud.new_dotdot = dst_dir;
		err = ext2fs_dir_iterate2(f->fs, from_ino, 0, NULL,
					  update_dotdot_proc, &ud);
		if (err)
			goto fail;

		err = read_inode(f->fs, src_dir, &inode);
		if (err)
			goto fail;
		if (inode.i_links_count > 1)
			inode.i_links_count--;
		err = write_inode(f->fs, src_dir, &inode);
		if (err)
			goto fail;

		err = read_inode(f->fs, dst_dir, &inode);
		if (err)
			goto fail;
		inode.i_links_count++;
		err = write_inode(f->fs, dst_dir, &inode);
		if (err)
			goto fail;
	}

	err = update_ctime(f->fs, from_ino);
	if (!err)
		err = update_mtime(f->fs, dst_dir);
	if (err)
		goto fail;

	/* Remove the old name */
	err = ext2fs_unlink(f->fs, src_dir, src_name, from_ino, 0);
	if (!err)
		err = update_mtime(f->fs, src_dir);
	if (!err)
		err = ext2fs_flush2(f->fs, 0);
	if (err)
		goto fail;
	goto out;
fail:
	ret = xlate(err);
out:
	UNLOCK(f);
	return ret;
}

E4_API int e4_set_attr(e4_fs *f, uint32_t ino, uint32_t mask, uint32_t mode,
		       uint32_t uid, uint32_t gid, const e4_times *t)
{
	struct ext2_inode_large inode;
	errcode_t err;
	int ret = 0;

	LOCK(f);
	if (!writable(f)) {
		ret = EROFS;
		goto out;
	}
	err = read_inode(f->fs, ino, &inode);
	if (err)
		goto fail;

	if (mask & E4_SET_MODE)
		inode.i_mode = (__u16) ((inode.i_mode & LINUX_S_IFMT) |
					(mode & 07777));
	if (mask & (E4_SET_UID | E4_SET_GID))
		set_owner(&inode, (mask & E4_SET_UID) ? uid : inode_uid(inode),
			  (mask & E4_SET_GID) ? gid : inode_gid(inode));

	if (mask & (E4_SET_UID | E4_SET_GID | E4_SET_MODE) &&
	    !(mask & E4_SET_CTIME))
		touch_ctime(&inode);
	if (t) {
		if (mask & E4_SET_ATIME)
			SET_XTIME(&inode, i_atime, t->atime_sec, t->atime_nsec);
		if (mask & E4_SET_MTIME)
			SET_XTIME(&inode, i_mtime, t->mtime_sec, t->mtime_nsec);
		if (mask & E4_SET_CTIME)
			SET_XTIME(&inode, i_ctime, t->ctime_sec, t->ctime_nsec);
		if ((mask & E4_SET_CRTIME) && FITS(&inode, i_crtime))
			SET_XTIME(&inode, i_crtime, t->crtime_sec,
				  t->crtime_nsec);
	}
	err = write_inode(f->fs, ino, &inode);
	if (err)
		goto fail;
	goto out;
fail:
	ret = xlate(err);
out:
	UNLOCK(f);
	return ret;
}

/* ------------------------------------------------------------------ */
/* misc                                                                */
/* ------------------------------------------------------------------ */

E4_API const char *e4_strerror(int code)
{
	InitOnceExecuteOnce(&et_once, init_et, NULL, NULL);
	switch (code) {
	case 0:
		return "Success";
	case E4_EVOLUME_LOCK:
		return "The volume is in use and cannot be locked for writing";
	case E4_ENOTEXT:
		return "No ext2/ext3/ext4 filesystem found";
	case E4_EUNSUPP_RW:
		return "The filesystem uses features that cannot be written safely";
	case E4_ENEEDS_FSCK:
		return "The filesystem has errors: run e2fsck before writing";
	}
	if (code > 0 && code < E4_ERRNO_MAX)
		return strerror(code);
	return error_message((errcode_t) code);
}

E4_API const char *e4_version(void)
{
	return E4_VERSION;
}
