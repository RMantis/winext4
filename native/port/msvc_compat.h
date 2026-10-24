/*
 * POSIX / GCC compatibility shims needed to compile libext2fs with MSVC.
 * Force-included into every libext2fs translation unit via config.h.
 */
#ifndef WINEXT4_MSVC_COMPAT_H
#define WINEXT4_MSVC_COMPAT_H

#ifndef _CRT_SECURE_NO_WARNINGS
#define _CRT_SECURE_NO_WARNINGS
#endif
#ifndef _CRT_NONSTDC_NO_DEPRECATE
#define _CRT_NONSTDC_NO_DEPRECATE
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <basetsd.h>
#include <winsock2.h> /* struct timeval, htonl/ntohl */

#ifndef __attribute__
#define __attribute__(x)
#endif
#ifndef __inline__
#define __inline__ __inline
#endif
#ifndef __alignof__
#define __alignof__(x) _Alignof(x)
#endif
#ifndef __restrict__
#define __restrict__ __restrict
#endif

typedef SSIZE_T ssize_t;
typedef int uid_t;
typedef int gid_t;
typedef int pid_t;
typedef unsigned int mode_t;
typedef unsigned int blksize_t_compat;

#define strcasecmp _stricmp
#define strncasecmp _strnicmp

#ifndef S_ISDIR
#define S_ISDIR(m) (((m) & _S_IFMT) == _S_IFDIR)
#endif
#ifndef S_ISREG
#define S_ISREG(m) (((m) & _S_IFMT) == _S_IFREG)
#endif
#ifndef S_ISCHR
#define S_ISCHR(m) (((m) & _S_IFMT) == _S_IFCHR)
#endif
#ifndef S_ISBLK
#define S_ISBLK(m) 0
#endif
#ifndef S_ISLNK
#define S_ISLNK(m) 0
#endif

/* journal.c names the Unix manager for external journals; ours stands in. */
#define unix_io_manager windows_io_manager

#ifndef O_DIRECT
#define O_DIRECT 0
#endif
#ifndef O_SYNC
#define O_SYNC 0
#endif

#endif
