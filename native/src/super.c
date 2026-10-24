/*
 * super.c --- superblock details, tune2fs-style changes and a fast inode
 * count for the partition properties dialog.
 */
#include <windows.h>
#include <errno.h>
#include <stdio.h>
#include <time.h>

#include "ext2fs/ext2_fs.h"
#include "ext2fs/ext2fs.h"
#include "uuid/uuid.h"
#include "ext4core.h"
#include "internal.h"

static int64_t tstamp(__u32 lo, __u8 hi)
{
	return lo ? (int64_t) lo | (int64_t) hi << 32 : 0;
}

static void copy_func(char *dst, const __u8 *src)
{
	memcpy(dst, src, 32);
	dst[32] = 0;
}

E4_API int e4_get_super(e4_fs *f, e4_super *o)
{
	struct ext2_super_block *sb;
	ext2_filsys fs;
	struct ext2_inode inode;
	dgrp_t g;

	memset(o, 0, sizeof(*o));
	e4_lock(f);
	fs = e4_filsys(f);
	sb = fs->super;
	o->mkfs_time = ext2fs_get_tstamp(sb, s_mkfs_time);
	o->last_check = ext2fs_get_tstamp(sb, s_lastcheck);
	o->first_error_time = tstamp(sb->s_first_error_time, sb->s_first_error_time_hi);
	o->last_error_time = tstamp(sb->s_last_error_time, sb->s_last_error_time_hi);
	o->mnt_count = sb->s_mnt_count;
	o->max_mnt_count = (int16_t) sb->s_max_mnt_count;
	o->check_interval = sb->s_checkinterval;
	o->errors_behavior = sb->s_errors;
	o->default_mount_opts = sb->s_default_mount_opts;
	o->reserved_uid = sb->s_def_resuid;
	o->reserved_gid = sb->s_def_resgid;
	o->creator_os = sb->s_creator_os;
	o->rev_level = sb->s_rev_level;
	o->minor_rev = sb->s_minor_rev_level;
	o->blocks_per_group = sb->s_blocks_per_group;
	o->inodes_per_group = sb->s_inodes_per_group;
	o->group_count = fs->group_desc_count;
	o->journal_inum = sb->s_journal_inum;
	if (sb->s_journal_inum &&
	    !ext2fs_read_inode(fs, sb->s_journal_inum, &inode))
		o->journal_size = EXT2_I_SIZE(&inode);
	for (g = 0; g < fs->group_desc_count; g++)
		o->dir_count += ext2fs_bg_used_dirs_count(fs, g);
	o->error_count = sb->s_error_count;
	o->first_error_ino = sb->s_first_error_ino;
	o->last_error_ino = sb->s_last_error_ino;
	o->first_error_line = sb->s_first_error_line;
	o->last_error_line = sb->s_last_error_line;
	o->first_error_block = sb->s_first_error_block;
	o->last_error_block = sb->s_last_error_block;
	copy_func(o->first_error_func, sb->s_first_error_func);
	copy_func(o->last_error_func, sb->s_last_error_func);
	o->kbytes_written = sb->s_kbytes_written;
	o->log_groups_per_flex = sb->s_log_groups_per_flex;
	o->flags = sb->s_flags;
	memcpy(o->mount_opts, sb->s_mount_opts, 64);
	uuid_unparse((const unsigned char *) sb->s_hash_seed, o->hash_seed);
	o->has_csum_seed = ext2fs_has_feature_csum_seed(sb);
	if (ext2fs_has_feature_stable_inodes(sb))
		o->uuid_change = 2;
	else if (!ext2fs_has_feature_csum_seed(sb) &&
		 (ext2fs_has_feature_metadata_csum(sb) || ext2fs_has_feature_ea_inode(sb)))
		o->uuid_change = 1;
	e4_unlock(f);
	return 0;
}

static int parse_uuid(const char *s, unsigned char *u)
{
	unsigned int b[16];
	int i;

	if (sscanf(s, "%2x%2x%2x%2x-%2x%2x-%2x%2x-%2x%2x-%2x%2x%2x%2x%2x%2x",
		   &b[0], &b[1], &b[2], &b[3], &b[4], &b[5], &b[6], &b[7], &b[8],
		   &b[9], &b[10], &b[11], &b[12], &b[13], &b[14], &b[15]) != 16)
		return -1;
	for (i = 0; i < 16; i++)
		u[i] = (unsigned char) b[i];
	return 0;
}

/* tune2fs -U, without the full checksum rewrite (we refuse that case). */
static int set_uuid(ext2_filsys fs, const char *text)
{
	struct ext2_super_block *sb = fs->super;
	unsigned char uu[16];
	int set_csum = 0;
	dgrp_t i;

	if (ext2fs_has_feature_stable_inodes(sb))
		return EINVAL;
	if (!ext2fs_has_feature_csum_seed(sb) &&
	    (ext2fs_has_feature_metadata_csum(sb) || ext2fs_has_feature_ea_inode(sb)))
		return E4_EUNSUPP_RW;
	if (!text || !strcmp(text, "random"))
		uuid_generate(uu);
	else if (parse_uuid(text, uu))
		return EINVAL;

	if (ext2fs_has_group_desc_csum(fs)) {
		for (i = 0; i < fs->group_desc_count; i++)
			if (!ext2fs_group_desc_csum_verify(fs, i))
				break;
		set_csum = i >= fs->group_desc_count;
	}
	memcpy(sb->s_uuid, uu, 16);
	ext2fs_init_csum_seed(fs);
	if (set_csum) {
		for (i = 0; i < fs->group_desc_count; i++)
			ext2fs_group_desc_csum_set(fs, i);
		fs->flags &= ~EXT2_FLAG_SUPER_ONLY;
	}
	return 0;
}

E4_API int e4_set_super(e4_fs *f, int field, uint64_t value, const char *text)
{
	struct ext2_super_block *sb;
	ext2_filsys fs;
	errcode_t err;
	int ret = 0;

	e4_lock(f);
	fs = e4_filsys(f);
	sb = fs->super;
	if (!e4_writable(f)) {
		ret = EROFS;
		goto out;
	}
	switch (field) {
	case E4_SUPER_LABEL:
		if (!text || strlen(text) > sizeof(sb->s_volume_name)) {
			ret = ENAMETOOLONG;
			goto out;
		}
		memset(sb->s_volume_name, 0, sizeof(sb->s_volume_name));
		memcpy(sb->s_volume_name, text, strlen(text));
		break;
	case E4_SUPER_UUID:
		ret = set_uuid(fs, text);
		break;
	case E4_SUPER_RESERVED_BLOCKS:
		if (value > ext2fs_blocks_count(sb) / 2) {
			ret = EINVAL;
			goto out;
		}
		ext2fs_r_blocks_count_set(sb, value);
		break;
	case E4_SUPER_RESERVED_UID:
		sb->s_def_resuid = (__u16) value;
		break;
	case E4_SUPER_RESERVED_GID:
		sb->s_def_resgid = (__u16) value;
		break;
	case E4_SUPER_MAX_MNT_COUNT:
		sb->s_max_mnt_count = (__u16) (int16_t) (int32_t) value;
		break;
	case E4_SUPER_CHECK_INTERVAL:
		sb->s_checkinterval = (__u32) value;
		break;
	case E4_SUPER_MNT_COUNT:
		sb->s_mnt_count = (__u16) value;
		break;
	case E4_SUPER_ERRORS:
		if (value < 1 || value > 3) {
			ret = EINVAL;
			goto out;
		}
		sb->s_errors = (__u16) value;
		break;
	case E4_SUPER_DEFAULT_MOUNT_OPTS:
		sb->s_default_mount_opts = (__u32) value;
		break;
	case E4_SUPER_LAST_MOUNTED:
		memset(sb->s_last_mounted, 0, sizeof(sb->s_last_mounted));
		if (text)
			strncpy((char *) sb->s_last_mounted, text, sizeof(sb->s_last_mounted));
		break;
	case E4_SUPER_ENABLE_CSUM_SEED:
		if (!ext2fs_has_feature_metadata_csum(sb)) {
			ret = EINVAL;
			goto out;
		}
		if (!ext2fs_has_feature_csum_seed(sb)) {
			/* keep checksums valid: freeze the seed derived from the current UUID */
			sb->s_checksum_seed = fs->csum_seed;
			ext2fs_set_feature_csum_seed(sb);
		}
		break;
	default:
		ret = EINVAL;
		goto out;
	}
	if (!ret) {
		ext2fs_mark_super_dirty(fs);
		err = ext2fs_flush2(fs, 0);
		ret = err ? e4_xlate(err) : 0;
	}
out:
	e4_unlock(f);
	return ret;
}

E4_API int e4_count_inodes(e4_fs *f, volatile int32_t *cancel,
			   volatile uint64_t *progress, e4_counts *out)
{
	ext2_inode_scan scan;
	struct ext2_inode_large inode;
	ext2_filsys fs;
	ext2_ino_t ino;
	errcode_t err;
	int ret = 0;

	memset(out, 0, sizeof(*out));
	e4_lock(f);
	fs = e4_filsys(f);
	err = ext2fs_open_inode_scan(fs, 0, &scan);
	if (err) {
		e4_unlock(f);
		return e4_xlate(err);
	}
	ext2fs_inode_scan_flags(scan, EXT2_SF_SKIP_MISSING_ITABLE, 0);
	for (;;) {
		err = ext2fs_get_next_inode_full(scan, &ino, EXT2_INODE(&inode), sizeof(inode));
		if (err == EXT2_ET_BAD_BLOCK_IN_INODE_TABLE)
			continue;
		if (err) {
			ret = e4_xlate(err);
			break;
		}
		if (!ino)
			break;
		out->inodes_scanned++;
		if (progress)
			*progress = ino;
		if (cancel && *cancel) {
			ret = ECANCELED;
			break;
		}
		if (!inode.i_links_count || inode.i_dtime ||
		    (ino < EXT2_FIRST_INODE(fs->super) && ino != EXT2_ROOT_INO))
			continue;
		if (LINUX_S_ISREG(inode.i_mode)) {
			out->regular++;
			out->regular_bytes += EXT2_I_SIZE(&inode);
		} else if (LINUX_S_ISDIR(inode.i_mode)) {
			out->directories++;
		} else if (LINUX_S_ISLNK(inode.i_mode)) {
			out->symlinks++;
		} else if (inode.i_mode) {
			out->special++;
		}
	}
	ext2fs_close_inode_scan(scan);
	e4_unlock(f);
	return ret;
}
