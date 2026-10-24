/*
 * Hand-written config.h for building libext2fs with MSVC on Windows x64.
 * Only what the library itself needs; the programs (e2fsck, mke2fs...) are
 * not built.
 */
#ifndef WINEXT4_CONFIG_H
#define WINEXT4_CONFIG_H

#include "msvc_compat.h"

#define HAVE_STDLIB_H		1
#define HAVE_ERRNO_H		1
#define HAVE_SYS_STAT_H		1
#define HAVE_SYS_TYPES_H	1
#define HAVE_UNISTD_H		1
#define HAVE_MALLOC_H		1
#define HAVE_STRDUP		1
#define HAVE_STRNLEN		1
#define HAVE_TYPE_SSIZE_T	1
#define HAVE_GET_FILE_SIZE_EX	1

#define SIZEOF_SHORT		2
#define SIZEOF_INT		4
#define SIZEOF_LONG		4
#define SIZEOF_LONG_LONG	8
#define SIZEOF_OFF_T		8
#define SIZEOF_TIME_T		8

#define ENABLE_NLS		0
#define WORDS_BIGENDIAN_UNKNOWN	0

/* No thread-local/locking requirements: the wrapper serialises all calls. */

#endif
