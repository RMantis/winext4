/*
 * e4cli --- tiny command line front end to ext4core, used for testing.
 *
 *   e4cli disks
 *   e4cli scan  <image>
 *   e4cli info  <dev> <offset>
 *   e4cli ls    <dev> <offset> <path>
 *   e4cli cat   <dev> <offset> <path>
 *   e4cli put   <dev> <offset> <localfile> <dir> <name>
 *   e4cli mkdir <dev> <offset> <dir> <name>
 *   e4cli rm    <dev> <offset> <dir> <name>
 *   e4cli rmdir <dev> <offset> <dir> <name>
 *   e4cli mv    <dev> <offset> <srcdir> <name> <dstdir> <newname>
 *   e4cli ln    <dev> <offset> <dir> <name> <target>
 *   e4cli trunc <dev> <offset> <path> <size>
 *
 * <dev> is an image file or a device path such as \\.\Harddisk1Partition1.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <io.h>
#include <fcntl.h>
#include <windows.h>
#include "ext4core.h"

static wchar_t *widen(const char *s)
{
	int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
	wchar_t *w = malloc(n * sizeof(wchar_t));

	MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n);
	return w;
}

static int fail(const char *what, int err)
{
	fprintf(stderr, "%s: %s (%d)\n", what, e4_strerror(err), err);
	return 1;
}

static int __cdecl print_part(void *ctx, const e4_part *p)
{
	(void) ctx;
	printf("disk=%d part=%d offset=%llu length=%llu bus=%d removable=%d "
	       "ext=%d v%d label='%s' uuid=%s model='%s' style=%d mbr=0x%02x "
	       "type=%s id=%s name='%s' attr=0x%llx gpt=0x%x diskid=%s dev=%ls\n",
	       p->disk_number, p->partition_number,
	       (unsigned long long) p->offset, (unsigned long long) p->length,
	       p->bus_type, p->removable, p->is_ext, p->ext_version, p->label,
	       p->uuid, p->model, p->part_style, p->mbr_type, p->gpt_type,
	       p->gpt_id, p->gpt_name, (unsigned long long) p->gpt_attributes,
	       p->gpt_status, p->disk_id, p->device);
	return 0;
}

struct ls_ctx {
	e4_fs *fs;
};

static int __cdecl print_entry(void *ctx, const char *name, int len,
			       uint32_t ino, int type)
{
	struct ls_ctx *lc = ctx;
	e4_stat st;

	if (e4_stat_ino(lc->fs, ino, &st))
		memset(&st, 0, sizeof(st));
	printf("%7u %06o %5u %5u %12llu %lld.%09u %.*s\n", ino, st.mode, st.uid,
	       st.gid, (unsigned long long) st.size, (long long) st.mtime_sec,
	       st.mtime_nsec, len, name);
	(void) type;
	return 0;
}

static int open_fs(const char *dev, const char *off, int rw, e4_fs **fs)
{
	wchar_t *w = widen(dev);
	int err = e4_open(w, _strtoui64(off, NULL, 0), 0, rw ? E4_OPEN_RW : 0,
			  fs);

	free(w);
	return err;
}

static int dir_ino(e4_fs *fs, const char *path, uint32_t *ino)
{
	return e4_lookup(fs, path, ino);
}

static int run(int argc, char **argv)
{
	const char *cmd;
	e4_fs *fs = NULL;
	int err = 0, rw;
	uint32_t ino, dir, dir2;

	if (argc < 2) {
		fprintf(stderr, "usage: see e4cli.c\n");
		return 2;
	}
	cmd = argv[1];

	if (!strcmp(cmd, "disks")) {
		err = e4_enum_disks(print_part, NULL);
		return err ? fail("enum_disks", err) : 0;
	}
	if (!strcmp(cmd, "scan") && argc == 3) {
		wchar_t *w = widen(argv[2]);

		err = e4_enum_image(w, print_part, NULL);
		free(w);
		return err ? fail("enum_image", err) : 0;
	}
	if (!strcmp(cmd, "setpart") && argc >= 5) {
		/* e4cli setpart <container> <num> type=GUID|mbr=0x83|name=..|attr=0x.. */
		e4_part_edit e;
		wchar_t *w = widen(argv[2]);
		int i;

		memset(&e, 0, sizeof(e));
		for (i = 4; i < argc; i++) {
			if (!strncmp(argv[i], "type=", 5)) {
				e.mask |= E4_EDIT_GPT_TYPE;
				strncpy(e.gpt_type, argv[i] + 5, sizeof(e.gpt_type) - 1);
			} else if (!strncmp(argv[i], "mbr=", 4)) {
				e.mask |= E4_EDIT_MBR_TYPE;
				e.mbr_type = (int) strtol(argv[i] + 4, NULL, 0);
			} else if (!strncmp(argv[i], "name=", 5)) {
				e.mask |= E4_EDIT_GPT_NAME;
				strncpy(e.gpt_name, argv[i] + 5, sizeof(e.gpt_name) - 1);
			} else if (!strncmp(argv[i], "attr=", 5)) {
				e.mask |= E4_EDIT_GPT_ATTRIBUTES;
				e.gpt_attributes = _strtoui64(argv[i] + 5, NULL, 0);
			}
		}
		err = e4_edit_partition(w, atoi(argv[3]), &e);
		free(w);
		return err ? fail("setpart", err) : 0;
	}
	if (argc < 4)
		return 2;

	rw = strcmp(cmd, "info") && strcmp(cmd, "ls") && strcmp(cmd, "cat") &&
	     strcmp(cmd, "super");
	err = open_fs(argv[2], argv[3], rw, &fs);
	if (err)
		return fail("open", err);

	if (!strcmp(cmd, "info")) {
		e4_info in;

		e4_get_info(fs, &in);
		printf("label='%s' uuid=%s bs=%u blocks=%llu free=%llu "
		       "inodes=%u free_inodes=%u state=%u journal=%d "
		       "needs_recovery=%d\nblocker='%s'\nwarnings='%s'\n",
		       in.label, in.uuid, in.block_size,
		       (unsigned long long) in.blocks_count,
		       (unsigned long long) in.free_blocks, in.inodes_count,
		       in.free_inodes, in.state, in.has_journal,
		       in.needs_recovery, in.rw_blocker, in.warnings);
	} else if (!strcmp(cmd, "ls") && argc == 5) {
		struct ls_ctx lc = { fs };

		err = e4_lookup(fs, argv[4], &ino);
		if (!err)
			err = e4_readdir(fs, ino, print_entry, &lc);
	} else if (!strcmp(cmd, "cat") && argc == 5) {
		static char buf[65536];
		uint64_t off = 0;
		uint32_t got;

		_setmode(_fileno(stdout), _O_BINARY);
		err = e4_lookup(fs, argv[4], &ino);
		while (!err) {
			err = e4_read(fs, ino, off, buf, sizeof(buf), &got);
			if (err || !got)
				break;
			fwrite(buf, 1, got, stdout);
			off += got;
		}
	} else if (!strcmp(cmd, "put") && argc == 7) {
		static char buf[1 << 20];
		FILE *in = _wfopen(widen(argv[4]), L"rb");
		uint64_t off = 0;
		size_t n;
		uint32_t wr;

		if (!in)
			return fail("fopen", 2);
		err = dir_ino(fs, argv[5], &dir);
		if (!err) {
			err = e4_lookup_child(fs, dir, argv[6], &ino);
			if (err == 2 /* ENOENT */)
				err = e4_create(fs, dir, argv[6], 0644, 1000, 1000,
						&ino);
			else if (!err)
				err = e4_set_size(fs, ino, 0);
		}
		while (!err && (n = fread(buf, 1, sizeof(buf), in)) > 0) {
			err = e4_write(fs, ino, off, buf, (uint32_t) n, &wr);
			off += wr;
		}
		fclose(in);
	} else if (!strcmp(cmd, "mkdir") && argc == 6) {
		err = dir_ino(fs, argv[4], &dir);
		if (!err)
			err = e4_mkdir(fs, dir, argv[5], 0755, 1000, 1000, &ino);
	} else if (!strcmp(cmd, "rm") && argc == 6) {
		err = dir_ino(fs, argv[4], &dir);
		if (!err)
			err = e4_unlink(fs, dir, argv[5]);
	} else if (!strcmp(cmd, "rmdir") && argc == 6) {
		err = dir_ino(fs, argv[4], &dir);
		if (!err)
			err = e4_rmdir(fs, dir, argv[5]);
	} else if (!strcmp(cmd, "mv") && argc == 8) {
		err = dir_ino(fs, argv[4], &dir);
		if (!err)
			err = dir_ino(fs, argv[6], &dir2);
		if (!err)
			err = e4_rename(fs, dir, argv[5], dir2, argv[7], 1);
	} else if (!strcmp(cmd, "ln") && argc == 7) {
		err = dir_ino(fs, argv[4], &dir);
		if (!err)
			err = e4_symlink(fs, dir, argv[5], argv[6], 1000, 1000,
					 &ino);
	} else if (!strcmp(cmd, "super")) {
		e4_super s;
		e4_counts c;

		e4_get_super(fs, &s);
		printf("mkfs=%lld mnt=%u/%d interval=%u errors=%u opts=0x%x resuid=%u resgid=%u "
		       "groups=%u journal=%u/%llu dirs=%u err=%u csum_seed=%u uuid_change=%u hash_seed=%s\n",
		       (long long) s.mkfs_time, s.mnt_count, s.max_mnt_count, s.check_interval,
		       s.errors_behavior, s.default_mount_opts, s.reserved_uid, s.reserved_gid,
		       s.group_count, s.journal_inum, (unsigned long long) s.journal_size,
		       s.dir_count, s.error_count, s.has_csum_seed, s.uuid_change, s.hash_seed);
		err = e4_count_inodes(fs, NULL, NULL, &c);
		printf("files=%llu dirs=%llu links=%llu special=%llu bytes=%llu\n",
		       (unsigned long long) c.regular, (unsigned long long) c.directories,
		       (unsigned long long) c.symlinks, (unsigned long long) c.special,
		       (unsigned long long) c.regular_bytes);
	} else if (!strcmp(cmd, "tune") && argc == 6) {
		/* e4cli tune <dev> <off> <field number> <value or text> */
		const char *v = argv[5];

		err = e4_set_super(fs, atoi(argv[4]), _strtoui64(v, NULL, 0), v);
	} else if (!strcmp(cmd, "trunc") && argc == 6) {
		err = e4_lookup(fs, argv[4], &ino);
		if (!err)
			err = e4_set_size(fs, ino, _strtoui64(argv[5], NULL, 0));
	} else {
		fprintf(stderr, "bad command\n");
		err = 22;
	}

	if (err) {
		fail(cmd, err);
		e4_close(fs);
		return 1;
	}
	err = e4_close(fs);
	return err ? fail("close", err) : 0;
}

/* Arguments arrive as UTF-16; ext4core wants UTF-8. */
int wmain(int argc, wchar_t **wargv)
{
	char **argv = calloc(argc + 1, sizeof(char *));
	int i, n;

	for (i = 0; i < argc; i++) {
		n = WideCharToMultiByte(CP_UTF8, 0, wargv[i], -1, NULL, 0, NULL,
					NULL);
		argv[i] = malloc(n);
		WideCharToMultiByte(CP_UTF8, 0, wargv[i], -1, argv[i], n, NULL,
				    NULL);
	}
	return run(argc, argv);
}
