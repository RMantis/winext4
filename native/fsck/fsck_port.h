/*
 * Extra portability glue for building e2fsck (and the bits of lib/support
 * and lib/e2p it needs) with MSVC. Force-included after config.h.
 */
#ifndef WINEXT4_FSCK_PORT_H
#define WINEXT4_FSCK_PORT_H

#include "config.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <io.h>
#include <sal.h>
/* SAL defines __reserved, a field name in e2fsck's fscrypt structures */
#undef __reserved

#undef ENABLE_NLS		/* config.h defines it as 0, e2fsck tests #ifdef */
#define HAVE_GETOPT_H 1
#define HAVE_INTPTR_T 1
#define HAVE_SETJMP_H 1
#define HAVE_STRCASECMP 1

#define strcasecmp _stricmp
#define strncasecmp _strnicmp

#include <time.h>
#include <sys/time.h>	/* port: gettimeofday */
#define popen _popen
#define pclose _pclose
#define strtok_r strtok_s
static __inline struct tm *gmtime_r(const time_t *t, struct tm *tm)
{
	return gmtime_s(tm, t) ? NULL : tm;
}
static __inline struct tm *localtime_r(const time_t *t, struct tm *tm)
{
	return localtime_s(tm, t) ? NULL : tm;
}
void *sbrk(intptr_t increment);
int fork(void);
int daemon(int nochdir, int noclose);
int pipe(int fds[2]);
unsigned int sleep(unsigned int seconds);

/* e2fsck reads the device through WinExt4's I/O layer, see e4fsck.c */
#define ext2fs_get_device_size2 e4fsck_get_device_size2
int e4fsck_get_device_size2(const char *file, int blocksize, unsigned long long *retblocks);

#ifndef PATH_MAX
#define PATH_MAX 260
#endif
#ifndef NAME_MAX
#define NAME_MAX 255
#endif

/* memory size, for e2fsck's choice of bitmap and cache sizes */
#define _SC_PHYS_PAGES 1
#define _SC_PAGESIZE 2
#define sysconf e4fsck_sysconf
long e4fsck_sysconf(int name);

/* e4fsck points E2FSCK_CONFIG at e2fsck.conf next to the executable */
#define ROOT_SYSCONFDIR "/etc"

#endif
