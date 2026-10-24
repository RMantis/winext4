/*
 * The POSIX calls e2fsck makes that MSVC lacks: directory listing (config
 * directories), and stubs for what makes no sense here (forking a log
 * writer, the signal catcher, sbrk-based memory tracking).
 */
#include <windows.h>
#include <io.h>
#include <fcntl.h>
#include <errno.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#include "dirent.h"

struct winext4_dir {
	HANDLE h;
	WIN32_FIND_DATAA fd;
	int first;
	struct dirent ent;
};

DIR *opendir(const char *name)
{
	char pattern[MAX_PATH];
	DIR *d;

	if (strlen(name) + 3 > sizeof(pattern)) {
		errno = ENAMETOOLONG;
		return NULL;
	}
	strcpy(pattern, name);
	strcat(pattern, "\\*");
	d = calloc(1, sizeof(*d));
	if (!d)
		return NULL;
	d->h = FindFirstFileA(pattern, &d->fd);
	if (d->h == INVALID_HANDLE_VALUE) {
		free(d);
		errno = ENOENT;
		return NULL;
	}
	d->first = 1;
	return d;
}

struct dirent *readdir(DIR *d)
{
	if (!d->first && !FindNextFileA(d->h, &d->fd))
		return NULL;
	d->first = 0;
	strncpy(d->ent.d_name, d->fd.cFileName, sizeof(d->ent.d_name) - 1);
	return &d->ent;
}

int closedir(DIR *d)
{
	FindClose(d->h);
	free(d);
	return 0;
}

int fork(void)
{
	errno = ENOSYS;
	return -1;
}

int daemon(int nochdir, int noclose)
{
	(void) nochdir;
	(void) noclose;
	errno = ENOSYS;
	return -1;
}

int pipe(int fds[2])
{
	return _pipe(fds, 4096, _O_BINARY);
}

unsigned int sleep(unsigned int seconds)
{
	Sleep(seconds * 1000);
	return 0;
}

void *sbrk(intptr_t increment)
{
	(void) increment;
	return NULL;
}

/* e2fsck's crash handler prints a backtrace with glibc: nothing to set up. */
void sigcatcher_setup(void)
{
}

/* Only used to explain a bad superblock magic on an unusual device. */
int check_plausibility(const char *device, int flags, int *ret_is_dev)
{
	(void) device;
	(void) flags;
	if (ret_is_dev)
		*ret_is_dev = 0;
	return 1;
}
