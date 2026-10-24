/*
 * e4fsck: e2fsck from e2fsprogs running on WinExt4's I/O layer.
 *
 *   e4fsck [e2fsck options] [--offset N] [--length N]
 *          [--draft DELTA [--desc TEXT]] SOURCE
 *
 * SOURCE is an image file or a volume (\\.\HarddiskNPartitionM, ...). It
 * is opened like ext4core does: sector-aligned raw I/O, volume locked and
 * dismounted when writing. With --draft the source is only read and every
 * repair goes to the draft (created if missing), to be reviewed and
 * applied later. "-n" opens the source read-only.
 *
 * e2fsck runs as a separate process because it is a program, not a
 * library: it keeps global state and calls exit() on fatal errors. Exit
 * codes are e2fsck's (0 clean, 1 fixed, 4 left uncorrected, 8 error...).
 */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>

#include "ext2fs/ext2fs.h"
#include "ext4core.h"
#include "winio.h"
#include "posix/getopt.h"

int e2fsck_main(int argc, char *argv[]);

/* e2fsck opens the filesystem and the journal through unix_io_manager. */
io_manager unix_io_manager;
io_manager undo_io_manager;

errcode_t set_undo_io_backing_manager(io_manager manager)
{
	(void) manager;
	return EXT2_ET_UNIMPLEMENTED;
}

errcode_t set_undo_io_backup_file(char *file_name)
{
	(void) file_name;
	return EXT2_ET_UNIMPLEMENTED;
}

static w4_dev *g_dev;
static char g_name[64];

int e4fsck_get_device_size2(const char *file, int blocksize, unsigned long long *retblocks)
{
	if (!g_dev || strcmp(file, g_name) || blocksize <= 0)
		return EXT2_ET_UNIMPLEMENTED;
	*retblocks = g_dev->length / (unsigned) blocksize;
	return 0;
}

long e4fsck_sysconf(int name)
{
	MEMORYSTATUSEX m = { sizeof(m) };

	if (name == 2)	/* _SC_PAGESIZE */
		return 4096;
	if (name == 1 && GlobalMemoryStatusEx(&m))	/* _SC_PHYS_PAGES */
		return (long) (m.ullTotalPhys / 4096);
	return -1;
}

/* Commits a draft (and unlocks a volume) however e2fsck exits. */
static void close_dev(void)
{
	if (g_dev) {
		w4_dev_close(g_dev);
		g_dev = NULL;
	}
}

/* ------------------------------------------------------------------ */
/* getopt (e2fsck's option parsing)                                    */

char *optarg;
int optind = 1, opterr = 1, optopt;

int getopt(int argc, char *const argv[], const char *optstring)
{
	static int pos = 1;
	const char *o;
	char c;

	if (optind >= argc || argv[optind][0] != '-' || !argv[optind][1])
		return -1;
	if (!strcmp(argv[optind], "--")) {
		optind++;
		return -1;
	}
	c = argv[optind][pos];
	o = strchr(optstring, c);
	if (!o || c == ':') {
		optopt = c;
		if (opterr)
			fprintf(stderr, "%s: invalid option -- '%c'\n", argv[0], c);
		if (!argv[optind][++pos]) {
			optind++;
			pos = 1;
		}
		return '?';
	}
	if (o[1] == ':') {
		if (argv[optind][pos + 1]) {
			optarg = &argv[optind][pos + 1];
		} else if (optind + 1 < argc) {
			optarg = argv[++optind];
		} else {
			optopt = c;
			optind++;
			pos = 1;
			return optstring[0] == ':' ? ':' : '?';
		}
		optind++;
		pos = 1;
	} else if (!argv[optind][++pos]) {
		optind++;
		pos = 1;
	}
	return c;
}

/* ------------------------------------------------------------------ */

static char *utf8(const wchar_t *w)
{
	int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, NULL, 0, NULL, NULL);
	char *s = malloc(n > 0 ? n : 1);

	if (s)
		WideCharToMultiByte(CP_UTF8, 0, w, -1, s, n, NULL, NULL);
	return s;
}

static const char *open_error(int code)
{
	switch (code) {
	case E4_EVOLUME_LOCK:
		return "the volume is in use: close the programs using it (or run as administrator)";
	case E4_EDRAFT_MISMATCH:
		return "the draft was made from a different or changed source";
	case E4_EDRAFT_BAD:
		return "not a valid draft file";
	case E4_EDRAFT_APPLIED:
		return "the draft was already applied";
	default:
		return code < E4_ERRNO_MAX ? strerror(code) : "cannot open the source";
	}
}

/* e2fsck.conf next to the executable, if any (not /etc of the current drive) */
static void set_config(void)
{
	char exe[MAX_PATH + 32], *p;

	if (getenv("E2FSCK_CONFIG") || !GetModuleFileNameA(NULL, exe, MAX_PATH))
		return;
	p = strrchr(exe, '\\');
	if (!p)
		return;
	strcpy(p + 1, "e2fsck.conf");
	_putenv_s("E2FSCK_CONFIG", exe);
}

int wmain(int argc, wchar_t **wargv)
{
	char **av = calloc(argc + 2, sizeof(char *));
	const wchar_t *path = NULL, *delta = NULL, *desc = L"";
	uint64_t offset = 0, length = 0;
	int n = 0, readonly = 0, i, ret;
	w4_dev *dev;

	if (!av)
		return 8;
	av[n++] = "e2fsck";
	for (i = 1; i < argc; i++) {
		const wchar_t *a = wargv[i];

		if (!wcscmp(a, L"--offset") && i + 1 < argc)
			offset = _wcstoui64(wargv[++i], NULL, 10);
		else if (!wcscmp(a, L"--length") && i + 1 < argc)
			length = _wcstoui64(wargv[++i], NULL, 10);
		else if (!wcscmp(a, L"--draft") && i + 1 < argc)
			delta = wargv[++i];
		else if (!wcscmp(a, L"--desc") && i + 1 < argc)
			desc = wargv[++i];
		else if (a[0] == L'-') {
			/* "-n" alone or inside a group ("-fn") means read-only */
			if (a[1] != L'-' && wcschr(a, L'n') && !wcschr(a, L'C') && !wcschr(a, L'E'))
				readonly = 1;
			av[n++] = utf8(a);
			/* options with a value */
			if (wcschr(L"CEbBjlLPIzMN", a[wcslen(a) - 1]) && a[2] == 0 && i + 1 < argc)
				av[n++] = utf8(wargv[++i]);
		} else
			path = a;
	}
	if (!path) {
		fprintf(stderr, "usage: e4fsck [e2fsck options] [--offset N] [--length N] "
				"[--draft DELTA [--desc TEXT]] SOURCE\n");
		return 16;
	}

	if (delta) {
		w4_dev *base;

		ret = w4_dev_open(path, offset, length, 0, &base);
		if (!ret)
			ret = w4_dev_open_overlay(base, delta, desc, &dev);
	} else {
		ret = w4_dev_open(path, offset, length, readonly ? 0 : W4_DEV_WRITE, &dev);
	}
	if (ret) {
		fprintf(stderr, "e4fsck: %s\n", open_error(ret));
		return 8;
	}
	g_dev = dev;
	atexit(close_dev);
	set_config();
	w4_dev_name(dev, g_name, sizeof(g_name));
	unix_io_manager = w4_io_manager;
	av[n++] = g_name;
	av[n] = NULL;
	ret = e2fsck_main(n, av);
	close_dev();
	return ret;
}
