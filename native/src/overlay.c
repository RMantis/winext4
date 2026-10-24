/*
 * overlay.c --- draft mode: a block-level copy-on-write layer.
 *
 * Delta file layout (little endian):
 *   0      4 KB header (magic, chunk size, source length, source fingerprint,
 *          apply state, label/UUID/description, crc32c)
 *   4096.. records: 32-byte header + 4 KB data (type 1), or a 32-byte
 *          commit marker (type 2). Records are only ever appended.
 *
 * libext2fs flushes call ov_commit(): on reopen everything after the last
 * commit marker is discarded, so a draft always comes back in the state of
 * a completed flush, even after a crash or an unplugged disk.
 */
#include <windows.h>
#include <errno.h>
#include <stdio.h>
#include <time.h>

#include "ext2fs/ext2_fs.h"
#include "ext2fs/ext2fs.h"
#include "ext4core.h"
#include "uuid/uuid.h"
#include "overlay.h"

#define HDR_SIZE 4096
#define REC_HDR 32
#define REC_MAGIC 0x43525857u /* "WXRC" */
#define REC_DATA 1
#define REC_COMMIT 2
#define FP_BYTES (128 * 1024)

struct ov_header {
	char magic[8];			/* "WX4DRAFT" */
	uint32_t version;
	uint32_t chunk_size;
	uint64_t source_length;
	int64_t created;
	unsigned char fingerprint[64];
	uint32_t apply_state;		/* 0 none, 1 started, 2 done */
	uint32_t reserved;
	char label[20];
	char uuid[40];
	wchar_t desc[260];
};

struct rec {
	uint32_t magic;
	uint32_t type;
	uint64_t chunk;
	uint64_t seq;
	uint32_t crc;
	uint32_t reserved;
};

struct w4_overlay {
	HANDLE file;
	wchar_t *path;		/* for compaction */
	w4_dev *base;
	struct ov_header hdr;
	uint64_t *keys;		/* chunk + 1, 0 = empty */
	uint64_t *vals;		/* file offset of the record */
	size_t cap, count;
	uint64_t end;		/* append position */
	uint64_t commit_end;	/* records before this offset are committed */
	uint64_t seq;
	int dirty;		/* data records since the last commit */
};

/* ------------------------------------------------------------------ */
/* map                                                                 */
/* ------------------------------------------------------------------ */

static size_t slot(uint64_t key, size_t cap)
{
	key ^= key >> 33;
	key *= 0xff51afd7ed558ccdULL;
	key ^= key >> 33;
	return (size_t) key & (cap - 1);
}

static int map_put(w4_overlay *ov, uint64_t chunk, uint64_t off);

static int map_grow(w4_overlay *ov)
{
	uint64_t *ok = ov->keys, *ovl = ov->vals;
	size_t oc = ov->cap, i;

	ov->cap = oc ? oc * 2 : 1024;
	ov->keys = calloc(ov->cap, sizeof(uint64_t));
	ov->vals = calloc(ov->cap, sizeof(uint64_t));
	if (!ov->keys || !ov->vals)
		return ENOMEM;
	ov->count = 0;
	for (i = 0; i < oc; i++)
		if (ok[i])
			map_put(ov, ok[i] - 1, ovl[i]);
	free(ok);
	free(ovl);
	return 0;
}

static int map_put(w4_overlay *ov, uint64_t chunk, uint64_t off)
{
	size_t i;

	if ((ov->count + 1) * 10 > ov->cap * 7 && map_grow(ov))
		return ENOMEM;
	for (i = slot(chunk, ov->cap);; i = (i + 1) & (ov->cap - 1)) {
		if (!ov->keys[i]) {
			ov->keys[i] = chunk + 1;
			ov->vals[i] = off;
			ov->count++;
			return 0;
		}
		if (ov->keys[i] == chunk + 1) {
			ov->vals[i] = off;
			return 0;
		}
	}
}

static int map_get(w4_overlay *ov, uint64_t chunk, uint64_t *off)
{
	size_t i;

	if (!ov->cap)
		return 0;
	for (i = slot(chunk, ov->cap); ov->keys[i]; i = (i + 1) & (ov->cap - 1))
		if (ov->keys[i] == chunk + 1) {
			*off = ov->vals[i];
			return 1;
		}
	return 0;
}

/* ------------------------------------------------------------------ */
/* file I/O                                                            */
/* ------------------------------------------------------------------ */

static int fio(HANDLE h, uint64_t pos, void *buf, DWORD len, int write)
{
	OVERLAPPED o;
	DWORD done = 0;
	BOOL ok;

	memset(&o, 0, sizeof(o));
	o.Offset = (DWORD) pos;
	o.OffsetHigh = (DWORD) (pos >> 32);
	ok = write ? WriteFile(h, buf, len, &done, &o) : ReadFile(h, buf, len, &done, &o);
	return ok && done == len ? 0 : EIO;
}

static uint32_t hdr_crc(const unsigned char *page)
{
	return ext2fs_crc32c_le(~0U, page, HDR_SIZE - 4);
}

static int write_header(w4_overlay *ov)
{
	unsigned char page[HDR_SIZE];
	uint32_t crc;

	memset(page, 0, sizeof(page));
	memcpy(page, &ov->hdr, sizeof(ov->hdr));
	crc = hdr_crc(page);
	memcpy(page + HDR_SIZE - 4, &crc, 4);
	return fio(ov->file, 0, page, HDR_SIZE, 1);
}

static int read_header(HANDLE h, struct ov_header *hdr)
{
	unsigned char page[HDR_SIZE];
	uint32_t crc;

	if (fio(h, 0, page, HDR_SIZE, 0))
		return E4_EDRAFT_BAD;
	memcpy(&crc, page + HDR_SIZE - 4, 4);
	memcpy(hdr, page, sizeof(*hdr));
	if (memcmp(hdr->magic, "WX4DRAFT", 8) || hdr->version != 1 ||
	    hdr->chunk_size != OV_CHUNK || crc != hdr_crc(page))
		return E4_EDRAFT_BAD;
	return 0;
}

int ov_fingerprint(w4_dev *base, unsigned char out[64])
{
	size_t n = (size_t) (base->length < FP_BYTES ? base->length : FP_BYTES);
	unsigned char *buf = malloc(FP_BYTES + 8);
	errcode_t err;

	if (!buf)
		return ENOMEM;
	memset(buf, 0, FP_BYTES + 8);
	err = w4_dev_pread(base, 0, buf, n);
	memcpy(buf + FP_BYTES, &base->length, 8);
	if (!err)
		ext2fs_sha512(buf, FP_BYTES + 8, out);
	free(buf);
	return err ? EIO : 0;
}

/*
 * Rebuilds the map from the records. Data after the last commit marker
 * (an interrupted session) is dropped and the file truncated there.
 */
static int replay(w4_overlay *ov, uint64_t size, int truncate)
{
	struct rec r;
	unsigned char *data = malloc(OV_CHUNK);
	uint64_t pos = HDR_SIZE, committed = HDR_SIZE;
	uint64_t *pend_chunk = NULL, *pend_off = NULL;
	size_t npend = 0, cpend = 0, i;
	int ret = 0;

	if (!data)
		return ENOMEM;
	while (pos + REC_HDR <= size) {
		if (fio(ov->file, pos, &r, REC_HDR, 0) || r.magic != REC_MAGIC)
			break;
		if (r.type == REC_COMMIT) {
			for (i = 0; i < npend; i++)
				if ((ret = map_put(ov, pend_chunk[i], pend_off[i])) != 0)
					goto out;
			npend = 0;
			pos += REC_HDR;
			committed = pos;
			if (r.seq > ov->seq)
				ov->seq = r.seq;
			continue;
		}
		if (r.type != REC_DATA || pos + REC_HDR + OV_CHUNK > size ||
		    fio(ov->file, pos + REC_HDR, data, OV_CHUNK, 0) ||
		    ext2fs_crc32c_le(~0U, data, OV_CHUNK) != r.crc)
			break;
		if (npend == cpend) {
			cpend = cpend ? cpend * 2 : 256;
			pend_chunk = realloc(pend_chunk, cpend * sizeof(uint64_t));
			pend_off = realloc(pend_off, cpend * sizeof(uint64_t));
			if (!pend_chunk || !pend_off) {
				ret = ENOMEM;
				goto out;
			}
		}
		pend_chunk[npend] = r.chunk;
		pend_off[npend++] = pos;
		if (r.seq > ov->seq)
			ov->seq = r.seq;
		pos += REC_HDR + OV_CHUNK;
	}
	ov->end = committed;
	ov->commit_end = committed;
	if (truncate && committed < size) {
		LARGE_INTEGER li;

		li.QuadPart = (LONGLONG) committed;
		if (!SetFilePointerEx(ov->file, li, NULL, FILE_BEGIN) || !SetEndOfFile(ov->file))
			ret = EIO;
	}
out:
	free(data);
	free(pend_chunk);
	free(pend_off);
	return ret;
}

int ov_open(const wchar_t *delta, w4_dev *base, const wchar_t *desc, w4_overlay **out)
{
	w4_overlay *ov;
	unsigned char fp[64], sb[1024];
	LARGE_INTEGER size;
	int ret;

	*out = NULL;
	ov = calloc(1, sizeof(*ov));
	if (!ov)
		return ENOMEM;
	ov->base = base;
	ov->file = CreateFileW(delta, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ,
			       NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
	if (ov->file == INVALID_HANDLE_VALUE) {
		DWORD e = GetLastError();

		free(ov);
		return e == ERROR_SHARING_VIOLATION ? EBUSY : e == ERROR_ACCESS_DENIED ? EACCES : EIO;
	}
	ret = ov_fingerprint(base, fp);
	if (ret)
		goto fail;
	GetFileSizeEx(ov->file, &size);
	if (size.QuadPart == 0) {
		/* new draft */
		memcpy(ov->hdr.magic, "WX4DRAFT", 8);
		ov->hdr.version = 1;
		ov->hdr.chunk_size = OV_CHUNK;
		ov->hdr.source_length = base->length;
		ov->hdr.created = (int64_t) time(NULL);
		memcpy(ov->hdr.fingerprint, fp, 64);
		if (!w4_dev_pread(base, 1024, sb, sizeof(sb)) && sb[56] == 0x53 && sb[57] == 0xEF) {
			memcpy(ov->hdr.label, sb + 120, 16);
			uuid_unparse(sb + 104, ov->hdr.uuid);
		}
		if (desc)
			wcsncpy(ov->hdr.desc, desc, 259);
		ret = write_header(ov);
		if (ret)
			goto fail;
		ov->end = HDR_SIZE;
		ov->commit_end = HDR_SIZE;
	} else {
		ret = read_header(ov->file, &ov->hdr);
		if (ret)
			goto fail;
		if (ov->hdr.source_length != base->length ||
		    memcmp(ov->hdr.fingerprint, fp, 64)) {
			ret = E4_EDRAFT_MISMATCH;
			goto fail;
		}
		if (ov->hdr.apply_state) {
			ret = E4_EDRAFT_APPLIED;
			goto fail;
		}
		ret = replay(ov, (uint64_t) size.QuadPart, 1);
		if (ret)
			goto fail;
	}
	ov->path = _wcsdup(delta);
	*out = ov;
	return 0;
fail:
	CloseHandle(ov->file);
	free(ov->keys);
	free(ov->vals);
	free(ov);
	return ret;
}

errcode_t ov_read(w4_overlay *ov, uint64_t pos, void *buf, size_t len)
{
	char *p = buf;

	while (len) {
		uint64_t chunk = pos / OV_CHUNK, off;
		size_t in = (size_t) (pos % OV_CHUNK);
		size_t n = OV_CHUNK - in < len ? OV_CHUNK - in : len;

		if (map_get(ov, chunk, &off)) {
			if (fio(ov->file, off + REC_HDR + in, p, (DWORD) n, 0))
				return EXT2_ET_SHORT_READ;
		} else {
			/* contiguous untouched chunks are read from the source in one go */
			size_t run = n;

			while (run < len) {
				uint64_t o2;
				size_t m = len - run < OV_CHUNK ? len - run : OV_CHUNK;

				if (map_get(ov, (pos + run) / OV_CHUNK, &o2))
					break;
				run += m;
			}
			if (w4_dev_pread(ov->base, pos, p, run))
				return EXT2_ET_SHORT_READ;
			n = run;
		}
		pos += n;
		p += n;
		len -= n;
	}
	return 0;
}

errcode_t ov_write(w4_overlay *ov, uint64_t pos, const void *buf, size_t len)
{
	const char *p = buf;
	unsigned char *rec = malloc(REC_HDR + OV_CHUNK);
	errcode_t err = 0;

	if (!rec)
		return EXT2_ET_NO_MEMORY;
	while (len) {
		uint64_t chunk = pos / OV_CHUNK;
		size_t in = (size_t) (pos % OV_CHUNK);
		size_t n = OV_CHUNK - in < len ? OV_CHUNK - in : len;
		unsigned char *data = rec + REC_HDR;
		struct rec r;

		if (in || n < OV_CHUNK) {
			/* partial chunk: start from the current content */
			uint64_t base_len = ov->base->length - chunk * OV_CHUNK;
			size_t valid = base_len < OV_CHUNK ? (size_t) base_len : OV_CHUNK;

			memset(data, 0, OV_CHUNK);
			err = ov_read(ov, chunk * OV_CHUNK, data, valid);
			if (err)
				break;
		}
		memcpy(data + in, p, n);
		r.magic = REC_MAGIC;
		r.type = REC_DATA;
		r.chunk = chunk;
		r.seq = ++ov->seq;
		r.crc = ext2fs_crc32c_le(~0U, data, OV_CHUNK);
		r.reserved = 0;
		memcpy(rec, &r, REC_HDR);
		{
			/*
			 * A chunk already rewritten since the last commit is updated
			 * in place: uncommitted records are dropped after a crash
			 * anyway, so nothing is lost, and metadata rewritten many
			 * times between two flushes does not bloat the file.
			 */
			uint64_t at;
			int in_place = map_get(ov, chunk, &at) && at >= ov->commit_end;

			if (!in_place)
				at = ov->end;
			if (fio(ov->file, at, rec, REC_HDR + OV_CHUNK, 1)) {
				err = EXT2_ET_SHORT_WRITE;
				break;
			}
			if (!in_place) {
				if (map_put(ov, chunk, at)) {
					err = EXT2_ET_NO_MEMORY;
					break;
				}
				ov->end += REC_HDR + OV_CHUNK;
			}
		}
		ov->dirty = 1;
		pos += n;
		p += n;
		len -= n;
	}
	free(rec);
	return err;
}

errcode_t ov_commit(w4_overlay *ov)
{
	struct rec r;

	if (!ov->dirty)
		return 0;
	memset(&r, 0, sizeof(r));
	r.magic = REC_MAGIC;
	r.type = REC_COMMIT;
	r.seq = ++ov->seq;
	if (fio(ov->file, ov->end, &r, REC_HDR, 1))
		return EXT2_ET_SHORT_WRITE;
	ov->end += REC_HDR;
	ov->commit_end = ov->end;
	ov->dirty = 0;
	return FlushFileBuffers(ov->file) ? 0 : EXT2_ET_SHORT_WRITE;
}

/*
 * Rewrites the delta with only the live records when more than half of it
 * is superseded data. The new file is written aside and swapped in with
 * MoveFileEx, so a crash leaves either the old or the new complete file.
 */
static void compact(w4_overlay *ov)
{
	uint64_t live = ov->count * (uint64_t) (REC_HDR + OV_CHUNK) + HDR_SIZE + REC_HDR;
	wchar_t tmp[MAX_PATH + 8];
	unsigned char *buf;
	HANDLE out;
	uint64_t pos = HDR_SIZE;
	size_t i;
	int ok = 1;

	if (!ov->path || ov->end < 2 * live || ov->end - live < (1 << 20) ||
	    wcslen(ov->path) > MAX_PATH)
		return;
	swprintf(tmp, MAX_PATH + 8, L"%ls.tmp", ov->path);
	out = CreateFileW(tmp, GENERIC_READ | GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
	if (out == INVALID_HANDLE_VALUE)
		return;
	buf = malloc(HDR_SIZE + OV_CHUNK);
	if (!buf || fio(ov->file, 0, buf, HDR_SIZE, 0) || fio(out, 0, buf, HDR_SIZE, 1))
		ok = 0;
	for (i = 0; ok && i < ov->cap; i++) {
		if (!ov->keys[i])
			continue;
		if (fio(ov->file, ov->vals[i], buf, REC_HDR + OV_CHUNK, 0) ||
		    fio(out, pos, buf, REC_HDR + OV_CHUNK, 1))
			ok = 0;
		pos += REC_HDR + OV_CHUNK;
	}
	if (ok) {
		struct rec r;

		memset(&r, 0, sizeof(r));
		r.magic = REC_MAGIC;
		r.type = REC_COMMIT;
		r.seq = ++ov->seq;
		ok = !fio(out, pos, &r, REC_HDR, 1) && FlushFileBuffers(out);
	}
	free(buf);
	CloseHandle(out);
	if (ok) {
		CloseHandle(ov->file);
		ov->file = INVALID_HANDLE_VALUE;
		if (!MoveFileExW(tmp, ov->path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
			DeleteFileW(tmp);
	} else {
		DeleteFileW(tmp);
	}
}

size_t ov_count(w4_overlay *ov)
{
	return ov->count;
}

void ov_close(w4_overlay *ov)
{
	if (!ov)
		return;
	ov_commit(ov);
	compact(ov);
	if (ov->file != INVALID_HANDLE_VALUE)
		CloseHandle(ov->file);
	free(ov->path);
	free(ov->keys);
	free(ov->vals);
	free(ov);
}

/* ------------------------------------------------------------------ */
/* draft API without an open filesystem                                */
/* ------------------------------------------------------------------ */

E4_API int e4_draft_get_info(const wchar_t *delta, e4_draft_info *out)
{
	w4_overlay tmp;
	LARGE_INTEGER size;
	int ret;

	memset(out, 0, sizeof(*out));
	memset(&tmp, 0, sizeof(tmp));
	/* read only and shared: the draft may be open in this very process */
	tmp.file = CreateFileW(delta, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
			       OPEN_EXISTING, 0, NULL);
	if (tmp.file == INVALID_HANDLE_VALUE)
		return ENOENT;
	ret = read_header(tmp.file, &tmp.hdr);
	if (!ret) {
		GetFileSizeEx(tmp.file, &size);
		ret = replay(&tmp, (uint64_t) size.QuadPart, 0);
	}
	if (!ret) {
		out->source_length = tmp.hdr.source_length;
		out->created = tmp.hdr.created;
		out->changed_chunks = tmp.count;
		out->chunk_size = OV_CHUNK;
		out->file_bytes = tmp.end;
		out->apply_state = tmp.hdr.apply_state;
		memcpy(out->label, tmp.hdr.label, sizeof(tmp.hdr.label));
		memcpy(out->uuid, tmp.hdr.uuid, sizeof(tmp.hdr.uuid));
		wcsncpy(out->source, tmp.hdr.desc, 259);
	}
	CloseHandle(tmp.file);
	free(tmp.keys);
	free(tmp.vals);
	return ret;
}

int ov_check(const wchar_t *delta, w4_dev *base)
{
	struct ov_header hdr;
	unsigned char fp[64];
	HANDLE h = CreateFileW(delta, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
			       OPEN_EXISTING, 0, NULL);
	int ret;

	if (h == INVALID_HANDLE_VALUE)
		return ENOENT;
	ret = read_header(h, &hdr);
	CloseHandle(h);
	if (ret)
		return ret;
	ret = ov_fingerprint(base, fp);
	if (ret)
		return ret;
	return hdr.source_length == base->length && !memcmp(hdr.fingerprint, fp, 64) ? 0 : E4_EDRAFT_MISMATCH;
}

static int cmp_u64(const void *a, const void *b)
{
	uint64_t x = *(const uint64_t *) a, y = *(const uint64_t *) b;

	return x < y ? -1 : x > y;
}

/*
 * Writes every committed chunk of @delta to @target. Idempotent: if it is
 * interrupted it can simply be run again (the fingerprint is then not
 * checked, the source already being partly updated).
 */
int ov_apply(const wchar_t *delta, w4_dev *target, volatile int32_t *cancel,
	     volatile uint64_t *progress)
{
	w4_overlay tmp;
	LARGE_INTEGER size;
	uint64_t *chunks = NULL;
	unsigned char *data = NULL, fp[64];
	size_t i, n = 0;
	int ret;

	memset(&tmp, 0, sizeof(tmp));
	tmp.file = CreateFileW(delta, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, NULL,
			       OPEN_EXISTING, 0, NULL);
	if (tmp.file == INVALID_HANDLE_VALUE)
		return GetLastError() == ERROR_SHARING_VIOLATION ? EBUSY : ENOENT;
	ret = read_header(tmp.file, &tmp.hdr);
	if (ret)
		goto out;
	if (tmp.hdr.source_length != target->length) {
		ret = E4_EDRAFT_MISMATCH;
		goto out;
	}
	if (tmp.hdr.apply_state == 0) {
		ret = ov_fingerprint(target, fp);
		if (ret)
			goto out;
		if (memcmp(fp, tmp.hdr.fingerprint, 64)) {
			ret = E4_EDRAFT_MISMATCH;
			goto out;
		}
	}
	GetFileSizeEx(tmp.file, &size);
	ret = replay(&tmp, (uint64_t) size.QuadPart, 1);
	if (ret)
		goto out;
	tmp.hdr.apply_state = 1;
	ret = write_header(&tmp);
	if (ret)
		goto out;
	FlushFileBuffers(tmp.file);

	chunks = malloc((tmp.count + 1) * sizeof(uint64_t));
	data = malloc(OV_CHUNK);
	if (!chunks || !data) {
		ret = ENOMEM;
		goto out;
	}
	for (i = 0; i < tmp.cap; i++)
		if (tmp.keys[i])
			chunks[n++] = tmp.keys[i] - 1;
	qsort(chunks, n, sizeof(uint64_t), cmp_u64);
	for (i = 0; i < n; i++) {
		uint64_t off, pos = chunks[i] * OV_CHUNK;
		size_t len = target->length - pos < OV_CHUNK ? (size_t) (target->length - pos) : OV_CHUNK;

		if (cancel && *cancel) {
			ret = ECANCELED;
			goto out;
		}
		map_get(&tmp, chunks[i], &off);
		if (fio(tmp.file, off + REC_HDR, data, OV_CHUNK, 0) ||
		    w4_dev_pwrite(target, pos, data, len)) {
			ret = EIO;
			goto out;
		}
		if (progress)
			*progress = i + 1;
	}
	if (target->writable && !FlushFileBuffers(target->h)) {
		ret = EIO;
		goto out;
	}
	tmp.hdr.apply_state = 2;
	ret = write_header(&tmp);
out:
	CloseHandle(tmp.file);
	free(tmp.keys);
	free(tmp.vals);
	free(chunks);
	free(data);
	return ret;
}
