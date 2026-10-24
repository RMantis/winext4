/*
 * parttable.c --- GPT/MBR helpers: CRC32, GUID text, GPT verification and
 * editing of partition type, name and attributes.
 */
#include <windows.h>
#include <winioctl.h>
#include <errno.h>
#include <stdio.h>
#include <string.h>

#include "ext4core.h"
#include "winio.h"
#include "parttable.h"

#define GPT_HDR_CRC 16
#define GPT_HDR_SIZE 12
#define GPT_CUR_LBA 24
#define GPT_BACKUP_LBA 32
#define GPT_ENTRIES_LBA 72
#define GPT_NUM_ENTRIES 80
#define GPT_ENTRY_SIZE 84
#define GPT_ENTRIES_CRC 88

static uint32_t crc_table[256];

static void crc_init(void)
{
	uint32_t i, j, c;

	for (i = 0; i < 256; i++) {
		c = i;
		for (j = 0; j < 8; j++)
			c = c & 1 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
		crc_table[i] = c;
	}
}

uint32_t pt_crc32(const void *data, size_t len)
{
	const unsigned char *p = data;
	uint32_t c = 0xFFFFFFFFu;

	if (!crc_table[1])
		crc_init();
	while (len--)
		c = crc_table[(c ^ *p++) & 0xFF] ^ (c >> 8);
	return c ^ 0xFFFFFFFFu;
}

static uint32_t rd32(const unsigned char *p)
{
	return (uint32_t) p[0] | (uint32_t) p[1] << 8 | (uint32_t) p[2] << 16 |
	       (uint32_t) p[3] << 24;
}

static uint64_t rd64(const unsigned char *p)
{
	return (uint64_t) rd32(p) | (uint64_t) rd32(p + 4) << 32;
}

static void wr32(unsigned char *p, uint32_t v)
{
	p[0] = (unsigned char) v;
	p[1] = (unsigned char) (v >> 8);
	p[2] = (unsigned char) (v >> 16);
	p[3] = (unsigned char) (v >> 24);
}

static void wr64(unsigned char *p, uint64_t v)
{
	wr32(p, (uint32_t) v);
	wr32(p + 4, (uint32_t) (v >> 32));
}

void pt_format_guid(const unsigned char *g, char *out)
{
	sprintf(out, "%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X",
		rd32(g), g[4] | g[5] << 8, g[6] | g[7] << 8, g[8], g[9],
		g[10], g[11], g[12], g[13], g[14], g[15]);
}

int pt_parse_guid(const char *s, unsigned char *g)
{
	unsigned int a, b, c, d[8];

	if (sscanf(s, "%8x-%4x-%4x-%2x%2x-%2x%2x%2x%2x%2x%2x", &a, &b, &c,
		   &d[0], &d[1], &d[2], &d[3], &d[4], &d[5], &d[6], &d[7]) != 11)
		return -1;
	wr32(g, a);
	g[4] = (unsigned char) b;
	g[5] = (unsigned char) (b >> 8);
	g[6] = (unsigned char) c;
	g[7] = (unsigned char) (c >> 8);
	for (a = 0; a < 8; a++)
		g[8 + a] = (unsigned char) d[a];
	return 0;
}

void pt_name_to_utf8(const unsigned char *utf16le, char *out, int out_size)
{
	wchar_t w[37];
	int i;

	for (i = 0; i < 36; i++)
		w[i] = (wchar_t) (utf16le[2 * i] | utf16le[2 * i + 1] << 8);
	w[36] = 0;
	if (!WideCharToMultiByte(CP_UTF8, 0, w, -1, out, out_size, NULL, NULL))
		out[0] = 0;
}

/* ------------------------------------------------------------------ */
/* verification                                                        */
/* ------------------------------------------------------------------ */

struct gpt_copy {
	unsigned char hdr[512];
	unsigned char *entries;
	uint32_t entries_len;
	int ok;
};

static void read_copy(const w4_reader *r, uint32_t ssz, uint64_t lba,
		      struct gpt_copy *c)
{
	unsigned char tmp[512];
	uint32_t hsize, n, esz;

	memset(c, 0, sizeof(*c));
	if (w4_read(r, lba * ssz, c->hdr, sizeof(c->hdr)) ||
	    memcmp(c->hdr, "EFI PART", 8))
		return;
	hsize = rd32(c->hdr + GPT_HDR_SIZE);
	if (hsize < 92 || hsize > sizeof(c->hdr))
		return;
	memcpy(tmp, c->hdr, hsize);
	wr32(tmp + GPT_HDR_CRC, 0);
	if (pt_crc32(tmp, hsize) != rd32(c->hdr + GPT_HDR_CRC))
		return;
	n = rd32(c->hdr + GPT_NUM_ENTRIES);
	esz = rd32(c->hdr + GPT_ENTRY_SIZE);
	if (esz < 128 || esz > 1024 || n > 4096)
		return;
	c->entries_len = n * esz;
	c->entries = malloc(c->entries_len);
	if (!c->entries ||
	    w4_read(r, rd64(c->hdr + GPT_ENTRIES_LBA) * ssz, c->entries,
		    c->entries_len))
		return;
	c->ok = pt_crc32(c->entries, c->entries_len) ==
		rd32(c->hdr + GPT_ENTRIES_CRC);
}

uint32_t pt_verify_gpt(const w4_reader *r, uint32_t ssz, uint64_t size)
{
	struct gpt_copy p, b;
	uint64_t backup_lba;
	uint32_t st = E4_GPT_CHECKED;

	read_copy(r, ssz, 1, &p);
	if (p.ok)
		st |= E4_GPT_PRIMARY_OK;
	backup_lba = p.ok ? rd64(p.hdr + GPT_BACKUP_LBA) : size / ssz - 1;
	if (backup_lba * ssz >= size)
		backup_lba = size / ssz - 1;
	read_copy(r, ssz, backup_lba, &b);
	if (b.ok)
		st |= E4_GPT_BACKUP_OK;
	if (p.ok && b.ok && p.entries_len == b.entries_len &&
	    !memcmp(p.entries, b.entries, p.entries_len) &&
	    !memcmp(p.hdr + 56, b.hdr + 56, 16) /* disk GUID */)
		st |= E4_GPT_COPIES_MATCH;
	free(p.entries);
	free(b.entries);
	return st;
}

/* ------------------------------------------------------------------ */
/* editing                                                             */
/* ------------------------------------------------------------------ */

static int utf8_to_gpt_name(const char *s, unsigned char *out72)
{
	wchar_t w[64];
	int n, i;

	n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s, -1, w, 64);
	if (n <= 0 || n - 1 > 36)
		return ENAMETOOLONG;
	memset(out72, 0, 72);
	for (i = 0; i < n - 1; i++) {
		out72[2 * i] = (unsigned char) w[i];
		out72[2 * i + 1] = (unsigned char) (w[i] >> 8);
	}
	return 0;
}

static int write_at(HANDLE h, uint64_t pos, const void *buf, DWORD len)
{
	OVERLAPPED ov;
	DWORD done;

	memset(&ov, 0, sizeof(ov));
	ov.Offset = (DWORD) pos;
	ov.OffsetHigh = (DWORD) (pos >> 32);
	return WriteFile(h, buf, len, &done, &ov) && done == len ? 0 : EIO;
}

/* Edits one entry in a GPT copy (header at @lba) and fixes both CRCs. */
static int edit_gpt_copy(HANDLE h, uint32_t ssz, uint64_t lba, int index,
			 const e4_part_edit *e, uint64_t *other_lba)
{
	struct gpt_copy c;
	unsigned char *ent, guid[16];
	uint32_t esz, hsize;
	int ret = 0;
	w4_reader rd = { h, 1, NULL };

	read_copy(&rd, ssz, lba, &c);
	if (!c.ok) {
		free(c.entries);
		return EIO;
	}
	*other_lba = rd64(c.hdr + GPT_BACKUP_LBA);
	esz = rd32(c.hdr + GPT_ENTRY_SIZE);
	if (index < 0 || (uint32_t) index >= rd32(c.hdr + GPT_NUM_ENTRIES)) {
		free(c.entries);
		return EINVAL;
	}
	ent = c.entries + (size_t) index * esz;
	if (e->mask & E4_EDIT_GPT_TYPE) {
		if (pt_parse_guid(e->gpt_type, guid)) {
			free(c.entries);
			return EINVAL;
		}
		memcpy(ent, guid, 16);
	}
	if (e->mask & E4_EDIT_GPT_NAME)
		ret = utf8_to_gpt_name(e->gpt_name, ent + 56);
	if (e->mask & E4_EDIT_GPT_ATTRIBUTES)
		wr64(ent + 48, e->gpt_attributes);
	if (!ret) {
		wr32(c.hdr + GPT_ENTRIES_CRC, pt_crc32(c.entries, c.entries_len));
		hsize = rd32(c.hdr + GPT_HDR_SIZE);
		wr32(c.hdr + GPT_HDR_CRC, 0);
		wr32(c.hdr + GPT_HDR_CRC, pt_crc32(c.hdr, hsize));
		ret = write_at(h, rd64(c.hdr + GPT_ENTRIES_LBA) * ssz, c.entries,
			       c.entries_len);
		if (!ret)
			ret = write_at(h, lba * ssz, c.hdr, 512);
	}
	free(c.entries);
	return ret;
}

static int edit_image(const wchar_t *path, int number, const e4_part_edit *e)
{
	HANDLE h;
	unsigned char mbr[512];
	uint32_t ssz;
	uint64_t backup;
	int ret = EINVAL, gpt = 0, i;

	h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ |
			FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
	if (h == INVALID_HANDLE_VALUE)
		return GetLastError() == ERROR_ACCESS_DENIED ? EACCES : ENOENT;
	if (w4_read_at(h, 0, mbr, 512, 1) || mbr[510] != 0x55 || mbr[511] != 0xAA)
		goto out;
	for (i = 0; i < 4; i++)
		if (mbr[446 + i * 16 + 4] == 0xEE)
			gpt = 1;

	if (gpt) {
		if (e->mask & E4_EDIT_MBR_TYPE)
			goto out;
		for (ssz = 512; ssz <= 4096; ssz *= 8) {
			unsigned char sig[8];

			if (!w4_read_at(h, ssz, sig, 8, 1) && !memcmp(sig, "EFI PART", 8))
				break;
		}
		if (ssz > 4096)
			goto out;
		ret = edit_gpt_copy(h, ssz, 1, number - 1, e, &backup);
		if (!ret)
			ret = edit_gpt_copy(h, ssz, backup, number - 1, e, &backup);
	} else {
		if (e->mask & ~E4_EDIT_MBR_TYPE || number < 1 || number > 4 ||
		    e->mbr_type <= 0 || e->mbr_type > 255)
			goto out;
		mbr[446 + (number - 1) * 16 + 4] = (unsigned char) e->mbr_type;
		ret = write_at(h, 0, mbr, 512);
	}
	if (!ret)
		FlushFileBuffers(h);
out:
	CloseHandle(h);
	return ret;
}

static int edit_disk(const wchar_t *path, int number, const e4_part_edit *e)
{
	DRIVE_LAYOUT_INFORMATION_EX *layout = NULL;
	DWORD size = 64 * 1024, ret_bytes, i;
	HANDLE h;
	int ret = EINVAL, found = 0;

	h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
			FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
	if (h == INVALID_HANDLE_VALUE)
		return GetLastError() == ERROR_ACCESS_DENIED ? EACCES : ENOENT;
	layout = malloc(size);
	if (!layout || !DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, NULL, 0,
					layout, size, &ret_bytes, NULL)) {
		ret = EIO;
		goto out;
	}
	for (i = 0; i < layout->PartitionCount; i++) {
		PARTITION_INFORMATION_EX *pi = &layout->PartitionEntry[i];

		if ((int) pi->PartitionNumber != number)
			continue;
		found = 1;
		if (layout->PartitionStyle == PARTITION_STYLE_GPT) {
			if (e->mask & E4_EDIT_MBR_TYPE)
				goto out;
			if ((e->mask & E4_EDIT_GPT_TYPE) &&
			    pt_parse_guid(e->gpt_type, (unsigned char *) &pi->Gpt.PartitionType))
				goto out;
			if (e->mask & E4_EDIT_GPT_NAME) {
				unsigned char name[72];
				int k;

				ret = utf8_to_gpt_name(e->gpt_name, name);
				if (ret)
					goto out;
				for (k = 0; k < 36; k++)
					pi->Gpt.Name[k] = (wchar_t) (name[2 * k] | name[2 * k + 1] << 8);
			}
			if (e->mask & E4_EDIT_GPT_ATTRIBUTES)
				pi->Gpt.Attributes = e->gpt_attributes;
		} else if (layout->PartitionStyle == PARTITION_STYLE_MBR) {
			if (e->mask & ~E4_EDIT_MBR_TYPE || e->mbr_type <= 0 || e->mbr_type > 255)
				goto out;
			pi->Mbr.PartitionType = (BYTE) e->mbr_type;
		}
		pi->RewritePartition = TRUE;
	}
	if (!found)
		goto out;
	ret = DeviceIoControl(h, IOCTL_DISK_SET_DRIVE_LAYOUT_EX, layout,
			      ret_bytes, NULL, 0, &ret_bytes, NULL) ? 0 :
	      GetLastError() == ERROR_ACCESS_DENIED ? EACCES : EIO;
	if (!ret)
		DeviceIoControl(h, IOCTL_DISK_UPDATE_PROPERTIES, NULL, 0, NULL, 0,
				&ret_bytes, NULL);
out:
	free(layout);
	CloseHandle(h);
	return ret;
}

E4_API int e4_edit_partition(const wchar_t *container, int partition_number,
			     const e4_part_edit *edit)
{
	if (!container || !edit || partition_number < 1)
		return EINVAL;
	if (_wcsnicmp(container, L"\\\\.\\PhysicalDrive", 17) == 0)
		return edit_disk(container, partition_number, edit);
	return edit_image(container, partition_number, edit);
}
