/* Minimal <sys/time.h> for MSVC builds of libext2fs. */
#ifndef WINEXT4_SYS_TIME_H
#define WINEXT4_SYS_TIME_H

#include <time.h>
#include <winsock2.h> /* struct timeval */

int winext4_gettimeofday(struct timeval *tv, void *tz);
#define gettimeofday winext4_gettimeofday

#endif
