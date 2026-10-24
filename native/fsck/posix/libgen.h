/* basename() for MSVC builds of e2fsck (accepts both separators). */
#ifndef WINEXT4_LIBGEN_H
#define WINEXT4_LIBGEN_H

#include <string.h>

static __inline char *basename(char *path)
{
	char *a = strrchr(path, '/'), *b = strrchr(path, '\\');
	char *p = a > b ? a : b;

	return p ? p + 1 : path;
}

#endif
