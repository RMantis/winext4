/*
 * Block device / image access for libext2fs on Windows.
 */
#ifndef WINEXT4_WINIO_H
#define WINEXT4_WINIO_H

#include <windows.h>
#include <stdint.h>
#include "ext2fs/ext2fs.h"

typedef struct w4_dev {
	HANDLE h;
	uint64_t offset;   /* start of the filesystem inside the handle */
	uint64_t length;   /* bytes available from offset */
	uint32_t align;    /* I/O alignment: 1 for files, sector size for devices */
	int writable;
	int is_device;
	int locked;        /* FSCTL_LOCK_VOLUME succeeded */
	DWORD last_error;  /* last Win32 error seen, for diagnostics */
} w4_dev;

#define W4_DEV_WRITE 0x1  /* open for writing (locks and dismounts a volume) */
#define W4_DEV_NOLOCK 0x2 /* caller takes care of locking (whole disks) */

/* Returns 0 or an ext4core error code (errno range or E4_*). */
int w4_dev_open(const wchar_t *path, uint64_t offset, uint64_t length,
		int flags, w4_dev **out);
void w4_dev_close(w4_dev *dev);

/* Positional I/O relative to dev->offset; handles sector alignment. */
errcode_t w4_dev_pread(w4_dev *dev, uint64_t pos, void *buf, size_t len);
errcode_t w4_dev_pwrite(w4_dev *dev, uint64_t pos, const void *buf,
			size_t len);

/* Raw read from an arbitrary handle at an absolute offset (for scanning). */
int w4_read_at(HANDLE h, uint64_t pos, void *buf, uint32_t len,
	       uint32_t align);

/*
 * io_manager whose channel name is "w4dev:<pointer>": the device must stay
 * open for as long as the ext2_filsys (and any re-open of it) lives.
 */
extern io_manager w4_io_manager;
void w4_dev_name(w4_dev *dev, char *buf, size_t size);

#endif
