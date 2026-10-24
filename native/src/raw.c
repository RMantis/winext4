/*
 * raw.c --- byte-level access to disks, partitions and image files, for
 * hashing, imaging and writing images back.
 *
 * Writing a whole physical disk requires every volume on it to be locked and
 * dismounted first (Windows rejects writes to sectors of mounted volumes);
 * those volume handles are held until e4_raw_close.
 */
#include <windows.h>
#include <winioctl.h>
#include <errno.h>
#include <stdio.h>

#include "ext4core.h"
#include "winio.h"

#define MAX_LOCKED 32

struct e4_raw {
	w4_dev *dev;
	HANDLE locked[MAX_LOCKED];
	int nlocked;
	int disk_number; /* >= 0 when the whole physical disk is open for writing */
};

static int disk_number_of(const wchar_t *path)
{
	int n;

	if (swscanf(path, L"\\\\.\\PhysicalDrive%d", &n) == 1)
		return n;
	return -1;
}

/* Disk numbers a volume lives on; returns their count. */
static int volume_disks(HANDLE vol, int *disks, int max)
{
	union {
		VOLUME_DISK_EXTENTS v;
		unsigned char raw[sizeof(VOLUME_DISK_EXTENTS) + 16 * sizeof(DISK_EXTENT)];
	} u;
	DWORD ret, i;
	int n = 0;

	if (!DeviceIoControl(vol, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, NULL, 0,
			     &u, sizeof(u), &ret, NULL))
		return 0;
	for (i = 0; i < u.v.NumberOfDiskExtents && n < max; i++)
		disks[n++] = (int) u.v.Extents[i].DiskNumber;
	return n;
}

E4_API int e4_volume_disks(const wchar_t *volume, int *disks, int max)
{
	HANDLE h = CreateFileW(volume, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
			       NULL, OPEN_EXISTING, 0, NULL);
	int n;

	if (h == INVALID_HANDLE_VALUE)
		return -1;
	n = volume_disks(h, disks, max);
	CloseHandle(h);
	return n;
}

static void unlock_all(e4_raw *r)
{
	DWORD ret;
	int i;

	for (i = 0; i < r->nlocked; i++) {
		DeviceIoControl(r->locked[i], FSCTL_UNLOCK_VOLUME, NULL, 0, NULL,
				0, &ret, NULL);
		CloseHandle(r->locked[i]);
	}
	r->nlocked = 0;
}

/* Locks and dismounts every volume that has an extent on disk @disk. */
static int lock_disk_volumes(e4_raw *r, int disk)
{
	wchar_t name[MAX_PATH];
	HANDLE find;
	int ret = 0;

	find = FindFirstVolumeW(name, MAX_PATH);
	if (find == INVALID_HANDLE_VALUE)
		return 0;
	do {
		size_t len = wcslen(name);
		int disks[16], n, i, on = 0, tries;
		HANDLE v;
		DWORD out;

		if (len && name[len - 1] == L'\\')
			name[len - 1] = 0; /* CreateFile wants no trailing slash */
		v = CreateFileW(name, GENERIC_READ | GENERIC_WRITE,
				FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
				OPEN_EXISTING, 0, NULL);
		if (v == INVALID_HANDLE_VALUE)
			continue;
		n = volume_disks(v, disks, 16);
		for (i = 0; i < n; i++)
			if (disks[i] == disk)
				on = 1;
		if (!on) {
			CloseHandle(v);
			continue;
		}
		for (tries = 0; tries < 20; tries++) {
			if (DeviceIoControl(v, FSCTL_LOCK_VOLUME, NULL, 0, NULL, 0, &out, NULL))
				break;
			Sleep(100);
		}
		if (tries == 20 || r->nlocked == MAX_LOCKED) {
			CloseHandle(v);
			ret = E4_EVOLUME_LOCK;
			break;
		}
		DeviceIoControl(v, FSCTL_DISMOUNT_VOLUME, NULL, 0, NULL, 0, &out, NULL);
		r->locked[r->nlocked++] = v;
	} while (FindNextVolumeW(find, name, MAX_PATH));
	FindVolumeClose(find);
	if (ret)
		unlock_all(r);
	return ret;
}

E4_API int e4_raw_open(const wchar_t *path, uint64_t offset, uint64_t length,
		       uint32_t flags, e4_raw **out)
{
	e4_raw *r;
	int ret, disk = disk_number_of(path);
	int dflags = 0;

	*out = NULL;
	r = calloc(1, sizeof(*r));
	if (!r)
		return ENOMEM;
	r->disk_number = -1;
	if (flags & E4_RAW_WRITE) {
		dflags = W4_DEV_WRITE;
		if (disk >= 0) {
			ret = lock_disk_volumes(r, disk);
			if (ret) {
				free(r);
				return ret;
			}
			r->disk_number = disk;
			dflags |= W4_DEV_NOLOCK;
		}
	}
	ret = w4_dev_open(path, offset, length, dflags, &r->dev);
	if (ret) {
		unlock_all(r);
		free(r);
		return ret;
	}
	*out = r;
	return 0;
}

E4_API int e4_raw_size(e4_raw *r, uint64_t *size)
{
	*size = r->dev->length;
	return 0;
}

E4_API int e4_raw_read(e4_raw *r, uint64_t pos, void *buf, uint32_t len)
{
	return w4_dev_pread(r->dev, pos, buf, len) ? EIO : 0;
}

E4_API int e4_raw_write(e4_raw *r, uint64_t pos, const void *buf, uint32_t len)
{
	return w4_dev_pwrite(r->dev, pos, buf, len) ? EIO : 0;
}

E4_API int e4_raw_flush(e4_raw *r)
{
	return r->dev->writable && !FlushFileBuffers(r->dev->h) ? EIO : 0;
}

E4_API int e4_raw_close(e4_raw *r)
{
	DWORD ret;

	if (!r)
		return 0;
	if (r->disk_number >= 0)
		/* the partition table may have changed: make Windows re-read it */
		DeviceIoControl(r->dev->h, IOCTL_DISK_UPDATE_PROPERTIES, NULL, 0,
				NULL, 0, &ret, NULL);
	w4_dev_close(r->dev);
	unlock_all(r);
	free(r);
	return 0;
}
