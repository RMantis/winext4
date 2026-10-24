/*
 * maintenance.c --- free space zero-fill (for compact images).
 */
#include <windows.h>
#include <errno.h>

#include "ext2fs/ext2_fs.h"
#include "ext2fs/ext2fs.h"
#include "ext4core.h"
#include "internal.h"

#define ZERO_CHUNK_BYTES (4 * 1024 * 1024)

static int is_zero(const char *p, size_t n)
{
	const uint64_t *q = (const uint64_t *) p;
	size_t i;

	for (i = 0; i < n / 8; i++)
		if (q[i])
			return 0;
	return 1;
}

E4_API int e4_zero_free(e4_fs *f, volatile int32_t *cancel,
			volatile uint64_t *progress, e4_zero_stats *out)
{
	ext2_filsys fs;
	blk64_t start, end, first, run_end, blk;
	char *buf = NULL, *zero = NULL;
	unsigned int bs, chunk;
	errcode_t err = 0;
	int ret = 0;

	memset(out, 0, sizeof(*out));
	e4_lock(f);
	fs = e4_filsys(f);
	if (!e4_writable(f)) {
		ret = EROFS;
		goto out;
	}
	if (!fs->block_map) {
		err = ext2fs_read_block_bitmap(fs);
		if (err)
			goto out;
	}
	bs = fs->blocksize;
	chunk = ZERO_CHUNK_BYTES / bs;
	buf = malloc((size_t) chunk * bs);
	zero = calloc(chunk, bs);
	if (!buf || !zero) {
		ret = ENOMEM;
		goto out;
	}

	start = fs->super->s_first_data_block;
	end = ext2fs_blocks_count(fs->super) - 1;
	while (start <= end) {
		/* next run of free blocks: [first, run_end) */
		err = ext2fs_find_first_zero_block_bitmap2(fs->block_map, start, end, &first);
		if (err == ENOENT) {
			err = 0;
			break;
		}
		if (err)
			goto out;
		err = ext2fs_find_first_set_block_bitmap2(fs->block_map, first, end, &run_end);
		if (err == ENOENT) {
			err = 0;
			run_end = end + 1;
		} else if (err) {
			goto out;
		}

		for (blk = first; blk < run_end; ) {
			unsigned int n = (unsigned int) (run_end - blk < chunk ? run_end - blk : chunk);
			unsigned int i = 0;

			if (cancel && *cancel) {
				ret = ECANCELED;
				goto out;
			}
			err = io_channel_read_blk64(fs->io, blk, (int) n, buf);
			if (err)
				goto out;
			/* write back only the stretches that are not already zero */
			while (i < n) {
				unsigned int j;

				if (is_zero(buf + (size_t) i * bs, bs)) {
					i++;
					continue;
				}
				for (j = i + 1; j < n && !is_zero(buf + (size_t) j * bs, bs); j++)
					;
				err = io_channel_write_blk64(fs->io, blk + i, (int) (j - i), zero);
				if (err)
					goto out;
				out->zeroed_blocks += j - i;
				i = j;
			}
			blk += n;
			out->free_blocks += n;
			if (progress)
				*progress = out->free_blocks;
		}
		start = run_end;
	}
	err = io_channel_flush(fs->io);
out:
	free(buf);
	free(zero);
	e4_unlock(f);
	if (!ret && err)
		ret = e4_xlate(err);
	return ret;
}
