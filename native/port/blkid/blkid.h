/*
 * Stub libblkid: only used by debugfs/journal.c to locate *external*
 * journal devices, which WinExt4 does not support.
 */
#ifndef WINEXT4_BLKID_H
#define WINEXT4_BLKID_H

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
	(void) cache; (void) token; (void) value;
	return 0;
}
static __inline char *blkid_devno_to_devname(unsigned long devno)
{
	(void) devno;
	return 0;
}

#endif
