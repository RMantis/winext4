/*
 * Stub libblkid: used to locate *external* journal devices, which WinExt4
 * does not support, and by e2fsck to resolve its device argument (a plain
 * name is returned as is, LABEL=/UUID= specs are not resolved).
 */
#ifndef WINEXT4_BLKID_H
#define WINEXT4_BLKID_H

#include <string.h>

typedef struct blkid_struct_cache *blkid_cache;

static __inline int blkid_get_cache(blkid_cache *cache, const char *filename)
{
	(void) filename;
	*cache = 0;
	return -1;
}
static __inline void blkid_put_cache(blkid_cache cache) { (void) cache; }
static __inline char *blkid_get_devname(blkid_cache cache, const char *token,
					const char *value)
{
	(void) cache;
	if (value || !token || strchr(token, '='))
		return 0;
	return _strdup(token);
}
static __inline char *blkid_devno_to_devname(unsigned long devno)
{
	(void) devno;
	return 0;
}

#endif
