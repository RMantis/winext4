/*
 * compat.c --- small POSIX/libuuid pieces libext2fs expects on Windows.
 */
#include <windows.h>
#include <bcrypt.h>
#include <stdio.h>
#include <string.h>
#include <sys/time.h>
#include "uuid/uuid.h"

int winext4_gettimeofday(struct timeval *tv, void *tz)
{
	FILETIME ft;
	ULARGE_INTEGER t;

	(void) tz;
	GetSystemTimePreciseAsFileTime(&ft);
	t.LowPart = ft.dwLowDateTime;
	t.HighPart = ft.dwHighDateTime;
	/* 100ns ticks since 1601 -> microseconds since 1970 */
	t.QuadPart = (t.QuadPart - 116444736000000000ULL) / 10;
	tv->tv_sec = (long) (t.QuadPart / 1000000);
	tv->tv_usec = (long) (t.QuadPart % 1000000);
	return 0;
}

int uuid_is_null(const uuid_t uu)
{
	int i;

	for (i = 0; i < 16; i++)
		if (uu[i])
			return 0;
	return 1;
}

void uuid_unparse(const uuid_t uu, char *out)
{
	sprintf(out,
		"%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-"
		"%02x%02x%02x%02x%02x%02x",
		uu[0], uu[1], uu[2], uu[3], uu[4], uu[5], uu[6], uu[7],
		uu[8], uu[9], uu[10], uu[11], uu[12], uu[13], uu[14], uu[15]);
}

int uuid_compare(const uuid_t uu1, const uuid_t uu2)
{
	return memcmp(uu1, uu2, 16);
}

void uuid_generate(uuid_t out)
{
	BCryptGenRandom(NULL, out, 16, BCRYPT_USE_SYSTEM_PREFERRED_RNG);
	out[6] = (out[6] & 0x0f) | 0x40; /* version 4 */
	out[8] = (out[8] & 0x3f) | 0x80; /* RFC 4122 variant */
}

void uuid_clear(uuid_t uu)
{
	memset(uu, 0, 16);
}

void uuid_copy(uuid_t dst, const uuid_t src)
{
	memcpy(dst, src, 16);
}
