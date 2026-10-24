/* Minimal <unistd.h> for MSVC builds of libext2fs. */
#ifndef WINEXT4_UNISTD_H
#define WINEXT4_UNISTD_H

#include <io.h>
#include <process.h>
#include <stdlib.h>

#ifndef R_OK
#define R_OK 4
#define W_OK 2
#define X_OK 0
#define F_OK 0
#endif

#define getuid()  0
#define geteuid() 0
#define getgid()  0
#define getegid() 0

static __inline int getpagesize(void) { return 4096; }
static __inline int fsync(int fd) { return _commit(fd); }

#endif
