/*
 * winio.c --- Windows block device / image file access for libext2fs.
 *
 * A w4_dev wraps a HANDLE to either an image file or a raw volume
 * (\\.\HarddiskNPartitionM).  Raw volumes need sector aligned I/O, and
 * writing to them requires the volume to be locked and dismounted so that
 * Windows cannot touch it while we do.
 *
 * The io_manager keeps a small write-through block cache; it never holds
 * dirty data, so a crash can only lose what libext2fs itself had not yet
 * written.
 */
#include <windows.h>
#include <winioctl.h>
#include <stdio.h>
#include <errno.h>

#include "ext2fs/ext2_fs.h"
#include "ext2fs/ext2fs.h"
#include "ext4core.h"
#include "winio.h"

#define W4_BOUNCE_SIZE (1024 * 1024)
#define W4_CACHE_SLOTS 4096

static int is_device_path(const wchar_t *path)
{
	return wcsncmp(path, L"\\\\.\\", 4) == 0 ||
	       _wcsnicmp(path, L"\\\\?\\Volume", 10) == 0 ||
	       _wcsnicmp(path, L"\\\\?\\GLOBALROOT", 14) == 0;
}

static uint32_t device_sector_size(HANDLE h)
{
	DISK_GEOMETRY_EX geo;
	DWORD ret;

	if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, NULL, 0,
			    &geo, sizeof(geo), &ret, NULL) &&
	    geo.Geometry.BytesPerSector >= 512 &&
	    (geo.Geometry.BytesPerSector & (geo.Geometry.BytesPerSector - 1)) == 0)
		return geo.Geometry.BytesPerSector;
	return 512;
}

static int device_length(HANDLE h, uint64_t *len)
{
	GET_LENGTH_INFORMATION li;
	DWORD ret;

	if (!DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, NULL, 0, &li,
			     sizeof(li), &ret, NULL))
		return 0;
	*len = (uint64_t) li.Length.QuadPart;
	return 1;
}

static int is_elevated(void)
{
	TOKEN_ELEVATION te;
	HANDLE tok;
	DWORD len = 0;
	int elevated = 0;

	if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok)) {
		if (GetTokenInformation(tok, TokenElevation, &te, sizeof(te), &len))
			elevated = te.TokenIsElevated != 0;
		CloseHandle(tok);
	}
	return elevated;
}

static int win_to_errno(DWORD err)
{
	switch (err) {
	case ERROR_FILE_NOT_FOUND:
	case ERROR_PATH_NOT_FOUND:
		return ENOENT;
	case ERROR_ACCESS_DENIED:
		return EACCES;
	case ERROR_SHARING_VIOLATION:
	case ERROR_LOCK_VIOLATION:
		return EBUSY;
	case ERROR_WRITE_PROTECT:
		return EROFS;
	case ERROR_NOT_READY:
	case ERROR_DEV_NOT_EXIST:
		return ENODEV;
	default:
		return EIO;
	}
}

int w4_dev_open(const wchar_t *path, uint64_t offset, uint64_t length,
		int writable, w4_dev **out)
{
	w4_dev *dev;
	DWORD access = GENERIC_READ | (writable ? GENERIC_WRITE : 0);
	DWORD share, flags = 0, ret;
	uint64_t total = 0;
	int device = is_device_path(path);
	int i;

	*out = NULL;
	dev = calloc(1, sizeof(*dev));
	if (!dev)
		return ENOMEM;

	if (device) {
		/* Volumes must be opened shared; exclusivity comes from the lock. */
		share = FILE_SHARE_READ | FILE_SHARE_WRITE;
		if (writable)
			flags = FILE_FLAG_WRITE_THROUGH;
	} else {
		share = FILE_SHARE_READ | (writable ? 0 : FILE_SHARE_WRITE);
	}

	dev->h = CreateFileW(path, access, share, NULL, OPEN_EXISTING, flags,
			     NULL);
	if (dev->h == INVALID_HANDLE_VALUE) {
		DWORD werr = GetLastError();
		int e = win_to_errno(werr);

		/*
		 * An administrator is only denied a volume when another process
		 * holds it locked (e.g. another WinExt4 writing to it).
		 */
		if (device && werr == ERROR_ACCESS_DENIED && is_elevated())
			e = E4_EVOLUME_LOCK;
		free(dev);
		return e;
	}
	dev->is_device = device;
	dev->writable = writable;
	dev->offset = offset;

	if (device) {
		dev->align = device_sector_size(dev->h);
		if (!device_length(dev->h, &total)) {
			w4_dev_close(dev);
			return EIO;
		}
		if (writable) {
			/*
			 * Explorer or the shell may briefly hold the volume
			 * open right after the disk arrives; retry a little.
			 */
			for (i = 0; i < 20; i++) {
				if (DeviceIoControl(dev->h, FSCTL_LOCK_VOLUME,
						    NULL, 0, NULL, 0, &ret, NULL)) {
					dev->locked = 1;
					break;
				}
				Sleep(100);
			}
			if (!dev->locked) {
				w4_dev_close(dev);
				return E4_EVOLUME_LOCK;
			}
			DeviceIoControl(dev->h, FSCTL_DISMOUNT_VOLUME, NULL, 0,
					NULL, 0, &ret, NULL);
			DeviceIoControl(dev->h, FSCTL_ALLOW_EXTENDED_DASD_IO,
					NULL, 0, NULL, 0, &ret, NULL);
		}
	} else {
		LARGE_INTEGER sz;

		dev->align = 1;
		if (!GetFileSizeEx(dev->h, &sz)) {
			w4_dev_close(dev);
			return EIO;
		}
		total = (uint64_t) sz.QuadPart;
	}

	if (offset > total) {
		w4_dev_close(dev);
		return EINVAL;
	}
	if (length == 0 || length > total - offset)
		length = total - offset;
	dev->length = length;
	*out = dev;
	return 0;
}

void w4_dev_close(w4_dev *dev)
{
	DWORD ret;

	if (!dev)
		return;
	if (dev->h != INVALID_HANDLE_VALUE) {
		if (dev->writable)
			FlushFileBuffers(dev->h);
		if (dev->locked)
			DeviceIoControl(dev->h, FSCTL_UNLOCK_VOLUME, NULL, 0,
					NULL, 0, &ret, NULL);
		CloseHandle(dev->h);
	}
	free(dev);
}

static errcode_t raw_io(w4_dev *dev, uint64_t abspos, void *buf, DWORD len,
			int write)
{
	OVERLAPPED ov;
	DWORD done = 0;
	BOOL ok;

	memset(&ov, 0, sizeof(ov));
	ov.Offset = (DWORD) abspos;
	ov.OffsetHigh = (DWORD) (abspos >> 32);
	if (write)
		ok = WriteFile(dev->h, buf, len, &done, &ov);
	else
		ok = ReadFile(dev->h, buf, len, &done, &ov);
	if (!ok) {
		dev->last_error = GetLastError();
		return write ? EXT2_ET_SHORT_WRITE : EXT2_ET_SHORT_READ;
	}
	if (done != len) {
		if (!write && !dev->is_device) {
			/* Sparse/short image file: treat the tail as zeroes. */
			memset((char *) buf + done, 0, len - done);
			return 0;
		}
		return write ? EXT2_ET_SHORT_WRITE : EXT2_ET_SHORT_READ;
	}
	return 0;
}

static errcode_t dev_io(w4_dev *dev, uint64_t pos, void *buf, size_t len,
			int write)
{
	uint64_t abs, start, end, cur;
	uint32_t a = dev->align;
	char *bounce;
	errcode_t err = 0;

	if (len == 0)
		return 0;
	if (pos > dev->length || len > dev->length - pos)
		return write ? EXT2_ET_SHORT_WRITE : EXT2_ET_SHORT_READ;
	if (write && !dev->writable)
		return EXT2_ET_RO_FILSYS;

	abs = dev->offset + pos;

	if (a <= 1) {
		while (len) {
			DWORD n = (DWORD) (len > W4_BOUNCE_SIZE * 8 ?
					   W4_BOUNCE_SIZE * 8 : len);
			err = raw_io(dev, abs, buf, n, write);
			if (err)
				return err;
			abs += n;
			buf = (char *) buf + n;
			len -= n;
		}
		return 0;
	}

	/* Devices: go through an aligned bounce buffer. */
	bounce = _aligned_malloc(W4_BOUNCE_SIZE, 4096);
	if (!bounce)
		return EXT2_ET_NO_MEMORY;

	start = abs & ~((uint64_t) a - 1);
	end = (abs + len + a - 1) & ~((uint64_t) a - 1);
	for (cur = start; cur < end; ) {
		DWORD n = (DWORD) (end - cur > W4_BOUNCE_SIZE ?
				   W4_BOUNCE_SIZE : end - cur);
		uint64_t ds = cur > abs ? cur : abs;
		uint64_t de = cur + n < abs + len ? cur + n : abs + len;

		if (write) {
			if (ds > cur || de < cur + n) {
				/* Partially covered sectors: read-modify-write */
				err = raw_io(dev, cur, bounce, n, 0);
				if (err)
					break;
			}
			memcpy(bounce + (ds - cur),
			       (const char *) buf + (ds - abs), (size_t) (de - ds));
			err = raw_io(dev, cur, bounce, n, 1);
		} else {
			err = raw_io(dev, cur, bounce, n, 0);
			if (!err)
				memcpy((char *) buf + (ds - abs),
				       bounce + (ds - cur), (size_t) (de - ds));
		}
		if (err)
			break;
		cur += n;
	}
	_aligned_free(bounce);
	return err;
}

errcode_t w4_dev_pread(w4_dev *dev, uint64_t pos, void *buf, size_t len)
{
	return dev_io(dev, pos, buf, len, 0);
}

errcode_t w4_dev_pwrite(w4_dev *dev, uint64_t pos, const void *buf,
			size_t len)
{
	return dev_io(dev, pos, (void *) buf, len, 1);
}

int w4_read_at(HANDLE h, uint64_t pos, void *buf, uint32_t len,
	       uint32_t align)
{
	w4_dev tmp;

	memset(&tmp, 0, sizeof(tmp));
	tmp.h = h;
	tmp.offset = 0;
	tmp.length = UINT64_MAX;
	tmp.align = align;
	tmp.is_device = align > 1;
	return dev_io(&tmp, pos, buf, len, 0) ? -1 : 0;
}

/* ------------------------------------------------------------------ */
/* io_manager                                                          */
/* ------------------------------------------------------------------ */

struct w4_chan {
	w4_dev *dev;
	uint64_t *tags;		/* block number + 1; 0 means empty */
	char *data;		/* W4_CACHE_SLOTS * block_size */
	struct struct_io_stats stats;
};

void w4_dev_name(w4_dev *dev, char *buf, size_t size)
{
	snprintf(buf, size, "w4dev:%p", (void *) dev);
}

static void cache_free(struct w4_chan *c)
{
	free(c->tags);
	free(c->data);
	c->tags = NULL;
	c->data = NULL;
}

static void cache_alloc(struct w4_chan *c, int blksize)
{
	cache_free(c);
	c->tags = calloc(W4_CACHE_SLOTS, sizeof(uint64_t));
	c->data = malloc((size_t) W4_CACHE_SLOTS * blksize);
	if (!c->tags || !c->data)
		cache_free(c);	/* run uncached rather than fail */
}

static errcode_t w4_open(const char *name, int flags, io_channel *channel)
{
	io_channel io = NULL;
	struct w4_chan *c = NULL;
	w4_dev *dev = NULL;
	errcode_t err;

	if (!name || sscanf(name, "w4dev:%p", (void **) &dev) != 1 || !dev)
		return EXT2_ET_BAD_DEVICE_NAME;
	if ((flags & IO_FLAG_RW) && !dev->writable)
		return EXT2_ET_RO_FILSYS;

	err = ext2fs_get_memzero(sizeof(struct struct_io_channel), &io);
	if (err)
		return err;
	err = ext2fs_get_memzero(sizeof(*c), &c);
	if (err)
		goto fail;
	err = ext2fs_get_mem(strlen(name) + 1, &io->name);
	if (err)
		goto fail;
	strcpy(io->name, name);

	io->magic = EXT2_ET_MAGIC_IO_CHANNEL;
	io->manager = w4_io_manager;
	io->block_size = 1024;
	io->refcount = 1;
	io->flags = CHANNEL_FLAGS_WRITETHROUGH;
	if (dev->is_device)
		io->flags |= CHANNEL_FLAGS_BLOCK_DEVICE;
	io->private_data = c;
	c->dev = dev;
	c->stats.num_fields = 4;
	cache_alloc(c, io->block_size);
	*channel = io;
	return 0;

fail:
	if (c)
		ext2fs_free_mem(&c);
	if (io)
		ext2fs_free_mem(&io);
	return err;
}

static errcode_t w4_close(io_channel channel)
{
	struct w4_chan *c = channel->private_data;

	if (--channel->refcount > 0)
		return 0;
	/* The w4_dev is owned by the e4_fs, not by the channel. */
	cache_free(c);
	ext2fs_free_mem(&c);
	ext2fs_free_mem(&channel->name);
	ext2fs_free_mem(&channel);
	return 0;
}

static errcode_t w4_set_blksize(io_channel channel, int blksize)
{
	struct w4_chan *c = channel->private_data;

	if (channel->block_size != blksize) {
		channel->block_size = blksize;
		cache_alloc(c, blksize);
	}
	return 0;
}

static errcode_t w4_read_blk64(io_channel channel, unsigned long long block,
			       int count, void *buf)
{
	struct w4_chan *c = channel->private_data;
	size_t bs = channel->block_size;
	size_t size = count < 0 ? (size_t) -count : (size_t) count * bs;
	uint64_t pos = block * bs;
	errcode_t err;
	int i;

	c->stats.bytes_read += size;

	if (count == 1 && c->tags) {
		unsigned slot = (unsigned) (block % W4_CACHE_SLOTS);
		char *p = c->data + (size_t) slot * bs;

		if (c->tags[slot] == block + 1) {
			c->stats.cache_hits++;
			memcpy(buf, p, bs);
			return 0;
		}
		c->stats.cache_misses++;
		err = w4_dev_pread(c->dev, pos, p, bs);
		if (err) {
			c->tags[slot] = 0;
			goto error;
		}
		c->tags[slot] = block + 1;
		memcpy(buf, p, bs);
		return 0;
	}

	err = w4_dev_pread(c->dev, pos, buf, size);
	if (err)
		goto error;
	if (count > 1 && c->tags) {
		for (i = 0; i < count; i++) {
			unsigned slot = (unsigned) ((block + i) % W4_CACHE_SLOTS);

			memcpy(c->data + (size_t) slot * bs,
			       (char *) buf + (size_t) i * bs, bs);
			c->tags[slot] = block + i + 1;
		}
	}
	return 0;

error:
	if (channel->read_error)
		return channel->read_error(channel, (unsigned long) block, count,
					   buf, size, 0, err);
	return err;
}

static errcode_t w4_read_blk(io_channel channel, unsigned long block,
			     int count, void *buf)
{
	return w4_read_blk64(channel, block, count, buf);
}

static errcode_t w4_write_blk64(io_channel channel, unsigned long long block,
				int count, const void *buf)
{
	struct w4_chan *c = channel->private_data;
	size_t bs = channel->block_size;
	size_t size = count < 0 ? (size_t) -count : (size_t) count * bs;
	uint64_t i, nblocks;
	errcode_t err;

	c->stats.bytes_written += size;
	err = w4_dev_pwrite(c->dev, block * bs, buf, size);

	if (c->tags) {
		nblocks = (size + bs - 1) / bs;
		for (i = 0; i < nblocks; i++) {
			unsigned slot = (unsigned) ((block + i) % W4_CACHE_SLOTS);

			if (!err && count > 0) {
				memcpy(c->data + (size_t) slot * bs,
				       (const char *) buf + i * bs, bs);
				c->tags[slot] = block + i + 1;
			} else if (c->tags[slot] == block + i + 1) {
				c->tags[slot] = 0;
			}
		}
	}

	if (err && channel->write_error)
		return channel->write_error(channel, (unsigned long) block,
					    count, buf, size, 0, err);
	return err;
}

static errcode_t w4_write_blk(io_channel channel, unsigned long block,
			      int count, const void *buf)
{
	return w4_write_blk64(channel, block, count, buf);
}

static errcode_t w4_write_byte(io_channel channel, unsigned long offset,
			       int size, const void *buf)
{
	struct w4_chan *c = channel->private_data;
	size_t bs = channel->block_size;
	uint64_t first = offset / bs, last = (offset + size - 1) / bs, b;
	errcode_t err;

	err = w4_dev_pwrite(c->dev, offset, buf, size);
	if (c->tags) {
		for (b = first; b <= last; b++) {
			unsigned slot = (unsigned) (b % W4_CACHE_SLOTS);

			if (c->tags[slot] == b + 1)
				c->tags[slot] = 0;
		}
	}
	return err;
}

static errcode_t w4_flush(io_channel channel)
{
	struct w4_chan *c = channel->private_data;

	if (c->dev->writable && !FlushFileBuffers(c->dev->h)) {
		c->dev->last_error = GetLastError();
		return EXT2_ET_SHORT_WRITE;
	}
	return 0;
}

static errcode_t w4_set_option(io_channel channel, const char *option,
			       const char *arg)
{
	(void) channel;
	(void) arg;
	if (!strcmp(option, "cache_blocks") || !strcmp(option, "cache"))
		return 0;
	return EXT2_ET_INVALID_ARGUMENT;
}

static errcode_t w4_get_stats(io_channel channel, io_stats *stats)
{
	struct w4_chan *c = channel->private_data;

	if (stats)
		*stats = &c->stats;
	return 0;
}

static struct struct_io_manager struct_w4_manager = {
	.magic = EXT2_ET_MAGIC_IO_MANAGER,
	.name = "WinExt4 I/O Manager",
	.open = w4_open,
	.close = w4_close,
	.set_blksize = w4_set_blksize,
	.read_blk = w4_read_blk,
	.write_blk = w4_write_blk,
	.flush = w4_flush,
	.write_byte = w4_write_byte,
	.set_option = w4_set_option,
	.get_stats = w4_get_stats,
	.read_blk64 = w4_read_blk64,
	.write_blk64 = w4_write_blk64,
};

io_manager w4_io_manager = &struct_w4_manager;
/* libext2fs refers to the platform default manager under this name. */
io_manager windows_io_manager = &struct_w4_manager;
