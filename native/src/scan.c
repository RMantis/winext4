/*
 * scan.c --- find ext2/3/4 filesystems on physical disks and image files.
 */
#include <windows.h>
#include <winioctl.h>
#include <errno.h>
#include <stdio.h>
#include <string.h>

#include "uuid/uuid.h"
#include "ext4core.h"
#include "winio.h"
#include "parttable.h"

#define SB_OFFSET 1024
#define SB_MAGIC_OFF 56
#define SB_COMPAT_OFF 92
#define SB_INCOMPAT_OFF 96
#define SB_UUID_OFF 104
#define SB_LABEL_OFF 120

static uint16_t le16(const unsigned char *p)
{
	return (uint16_t) (p[0] | p[1] << 8);
}

static uint32_t le32(const unsigned char *p)
{
	return (uint32_t) p[0] | (uint32_t) p[1] << 8 |
	       (uint32_t) p[2] << 16 | (uint32_t) p[3] << 24;
}

static uint64_t le64(const unsigned char *p)
{
	return (uint64_t) le32(p) | (uint64_t) le32(p + 4) << 32;
}

/* Reads the superblock at @offset; fills the ext fields of @part. */
static void probe(const w4_reader *r, uint64_t offset, e4_part *part)
{
	unsigned char buf[4096];
	const unsigned char *sb = buf + SB_OFFSET;
	uint32_t compat, incompat;

	part->is_ext = 0;
	if (w4_read(r, offset, buf, sizeof(buf)))
		return;
	if (le16(sb + SB_MAGIC_OFF) != 0xEF53)
		return;

	part->is_ext = 1;
	compat = le32(sb + SB_COMPAT_OFF);
	incompat = le32(sb + SB_INCOMPAT_OFF);
	if (incompat & (0x40 | 0x80 | 0x200)) /* extents, 64bit, flex_bg */
		part->ext_version = 4;
	else if (compat & 0x4)                /* has_journal */
		part->ext_version = 3;
	else
		part->ext_version = 2;
	memcpy(part->label, sb + SB_LABEL_OFF, 16);
	part->label[16] = 0;
	if (!uuid_is_null(sb + SB_UUID_OFF))
		uuid_unparse(sb + SB_UUID_OFF, part->uuid);
}

/* ------------------------------------------------------------------ */
/* physical disks                                                      */
/* ------------------------------------------------------------------ */

static void query_device(HANDLE h, e4_part *base)
{
	STORAGE_PROPERTY_QUERY q;
	union {
		STORAGE_DEVICE_DESCRIPTOR d;
		unsigned char raw[1024];
	} u;
	DWORD ret;
	char vendor[64] = "", product[64] = "";

	memset(&q, 0, sizeof(q));
	q.PropertyId = StorageDeviceProperty;
	q.QueryType = PropertyStandardQuery;
	memset(&u, 0, sizeof(u));
	if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, &q, sizeof(q),
			     &u, sizeof(u), &ret, NULL))
		return;
	base->bus_type = u.d.BusType;
	base->removable = u.d.RemovableMedia;
	if (u.d.VendorIdOffset && u.d.VendorIdOffset < sizeof(u))
		strncpy(vendor, (char *) u.raw + u.d.VendorIdOffset,
			sizeof(vendor) - 1);
	if (u.d.ProductIdOffset && u.d.ProductIdOffset < sizeof(u))
		strncpy(product, (char *) u.raw + u.d.ProductIdOffset,
			sizeof(product) - 1);
	/* trim trailing blanks the descriptors are padded with */
	for (ret = (DWORD) strlen(vendor); ret && vendor[ret - 1] == ' '; ret--)
		vendor[ret - 1] = 0;
	for (ret = (DWORD) strlen(product); ret && product[ret - 1] == ' '; ret--)
		product[ret - 1] = 0;
	snprintf(base->model, sizeof(base->model), "%s%s%s", vendor,
		 vendor[0] && product[0] ? " " : "", product);
}

static DRIVE_LAYOUT_INFORMATION_EX *get_layout(HANDLE h)
{
	DWORD size = 4096, ret;
	DRIVE_LAYOUT_INFORMATION_EX *layout;

	for (;;) {
		layout = malloc(size);
		if (!layout)
			return NULL;
		if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, NULL, 0,
				    layout, size, &ret, NULL))
			return layout;
		free(layout);
		if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || size > 1 << 20)
			return NULL;
		size *= 4;
	}
}

static int is_mbr_container(BYTE type)
{
	return type == 0x05 || type == 0x0F || type == 0x85;
}

E4_API int e4_enum_disks(e4_part_cb cb, void *ctx)
{
	int n, denied = 0, opened = 0;
	DWORD i;

	for (n = 0; n < 64; n++) {
		wchar_t path[64];
		HANDLE h;
		e4_part base, part;
		DRIVE_LAYOUT_INFORMATION_EX *layout;
		GET_LENGTH_INFORMATION li;
		DISK_GEOMETRY_EX geo;
		DWORD ret;
		uint32_t align = 512;
		int found = 0;

		swprintf(path, 64, L"\\\\.\\PhysicalDrive%d", n);
		h = CreateFileW(path, GENERIC_READ,
				FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
				OPEN_EXISTING, 0, NULL);
		if (h == INVALID_HANDLE_VALUE) {
			if (GetLastError() == ERROR_ACCESS_DENIED)
				denied = 1;
			continue;
		}
		opened++;

		memset(&base, 0, sizeof(base));
		wcscpy(base.container, path);
		base.disk_number = n;
		base.bus_type = 0;
		query_device(h, &base);
		if (DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, NULL, 0, &li,
				    sizeof(li), &ret, NULL))
			base.disk_size = (uint64_t) li.Length.QuadPart;
		if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, NULL, 0,
				    &geo, sizeof(geo), &ret, NULL) &&
		    geo.Geometry.BytesPerSector >= 512)
			align = geo.Geometry.BytesPerSector;
		base.sector_size = align;

		layout = get_layout(h);
		if (layout) {
			if (layout->PartitionStyle == PARTITION_STYLE_GPT) {
				base.part_style = 2;
				pt_format_guid((const unsigned char *) &layout->Gpt.DiskId, base.disk_id);
				w4_reader drd = { h, align, NULL };

				base.gpt_status = pt_verify_gpt(&drd, align, base.disk_size);
			} else if (layout->PartitionStyle == PARTITION_STYLE_MBR) {
				base.part_style = 1;
				sprintf(base.disk_id, "%08X", (unsigned) layout->Mbr.Signature);
			}
			for (i = 0; i < layout->PartitionCount; i++) {
				PARTITION_INFORMATION_EX *pi =
					&layout->PartitionEntry[i];

				if (pi->PartitionNumber == 0 ||
				    pi->PartitionLength.QuadPart == 0)
					continue;
				if (pi->PartitionStyle == PARTITION_STYLE_MBR &&
				    is_mbr_container(pi->Mbr.PartitionType))
					continue;
				part = base;
				part.partition_number = (int32_t) pi->PartitionNumber;
				swprintf(part.device, 96,
					 L"\\\\.\\Harddisk%dPartition%lu", n,
					 pi->PartitionNumber);
				wcscpy(part.lock_path, part.device);
				part.offset = 0;
				part.length = (uint64_t) pi->PartitionLength.QuadPart;
				if (pi->PartitionStyle == PARTITION_STYLE_GPT) {
					int k;
					unsigned char name[72];

					pt_format_guid((const unsigned char *) &pi->Gpt.PartitionType, part.gpt_type);
					pt_format_guid((const unsigned char *) &pi->Gpt.PartitionId, part.gpt_id);
					for (k = 0; k < 36; k++) {
						name[2 * k] = (unsigned char) pi->Gpt.Name[k];
						name[2 * k + 1] = (unsigned char) (pi->Gpt.Name[k] >> 8);
					}
					pt_name_to_utf8(name, part.gpt_name, sizeof(part.gpt_name));
					part.gpt_attributes = pi->Gpt.Attributes;
				} else if (pi->PartitionStyle == PARTITION_STYLE_MBR) {
					part.mbr_type = pi->Mbr.PartitionType;
				}
				w4_reader prd = { h, align, NULL };

				probe(&prd, (uint64_t) pi->StartingOffset.QuadPart, &part);
				found = 1;
				if (cb(ctx, &part)) {
					free(layout);
					CloseHandle(h);
					return 0;
				}
			}
			free(layout);
		}

		if (!found) {
			/* No partition table: maybe a filesystem on the bare disk */
			part = base;
			part.partition_number = 0;
			wcscpy(part.device, path);
			part.offset = 0;
			part.length = base.disk_size;
			w4_reader brd = { h, align, NULL };

			probe(&brd, 0, &part);
			if (part.is_ext && cb(ctx, &part)) {
				CloseHandle(h);
				return 0;
			}
		}
		CloseHandle(h);
	}
	if (!opened && denied)
		return EACCES;
	return 0;
}

/* ------------------------------------------------------------------ */
/* image files                                                         */
/* ------------------------------------------------------------------ */

struct img_ctx {
	w4_reader rd;
	e4_part table;	/* table-wide fields copied into every partition */
	const wchar_t *path;
	uint64_t size;
	e4_part_cb cb;
	void *ctx;
	int count;
	int stop;
};

static void emit(struct img_ctx *ic, int number, uint64_t offset,
		 uint64_t length, const unsigned char *gpt_entry, int mbr_type)
{
	e4_part part;

	if (ic->stop || offset >= ic->size)
		return;
	if (length == 0 || length > ic->size - offset)
		length = ic->size - offset;
	part = ic->table;
	wcsncpy(part.container, ic->path, 95);
	if (gpt_entry) {
		pt_format_guid(gpt_entry, part.gpt_type);
		pt_format_guid(gpt_entry + 16, part.gpt_id);
		part.gpt_attributes = le64(gpt_entry + 48);
		pt_name_to_utf8(gpt_entry + 56, part.gpt_name, sizeof(part.gpt_name));
	}
	part.mbr_type = mbr_type;
	wcsncpy(part.device, ic->path, 95);
	part.offset = offset;
	part.length = length;
	part.disk_number = -1;
	part.partition_number = number;
	part.bus_type = -1;
	part.disk_size = ic->size;
	probe(&ic->rd, offset, &part);
	ic->count++;
	if (ic->cb(ic->ctx, &part))
		ic->stop = 1;
}

static int scan_gpt(struct img_ctx *ic, uint32_t ssz)
{
	unsigned char hdr[512], ent[512];
	uint64_t table_lba;
	uint32_t count, esz, i;
	static const unsigned char zero[16];

	if (w4_read(&ic->rd, ssz, hdr, sizeof(hdr)) ||
	    memcmp(hdr, "EFI PART", 8))
		return 0;
	ic->table.part_style = 2;
	ic->table.sector_size = ssz;
	pt_format_guid(hdr + 56, ic->table.disk_id);
	ic->table.gpt_status = pt_verify_gpt(&ic->rd, ssz, ic->size);
	table_lba = le64(hdr + 72);
	count = le32(hdr + 80);
	esz = le32(hdr + 84);
	if (esz < 128 || esz > sizeof(ent) || count > 1024)
		return 0;
	for (i = 0; i < count && !ic->stop; i++) {
		uint64_t first, last;

		if (w4_read(&ic->rd, table_lba * ssz + (uint64_t) i * esz, ent, esz))
			break;
		if (!memcmp(ent, zero, 16))
			continue;
		first = le64(ent + 32);
		last = le64(ent + 40);
		if (last < first)
			continue;
		emit(ic, (int) i + 1, first * ssz, (last - first + 1) * ssz, ent, 0);
	}
	return 1;
}

static void scan_mbr(struct img_ctx *ic, const unsigned char *mbr)
{
	int i, number = 4;

	for (i = 0; i < 4 && !ic->stop; i++) {
		const unsigned char *e = mbr + 446 + i * 16;
		BYTE type = e[4];
		uint64_t start = le32(e + 8), len = le32(e + 12);

		if (type == 0 || len == 0)
			continue;
		if (!is_mbr_container(type)) {
			emit(ic, i + 1, start * 512, len * 512, NULL, type);
			continue;
		}
		/* Walk the chain of extended boot records (logical drives). */
		{
			uint64_t ext_base = start, ebr = start;
			int guard = 0;
			unsigned char sec[512];

			while (!ic->stop && guard++ < 128) {
				const unsigned char *l, *next;

				if (w4_read(&ic->rd, ebr * 512, sec, 512) ||
				    sec[510] != 0x55 || sec[511] != 0xAA)
					break;
				l = sec + 446;
				next = sec + 462;
				if (l[4] && le32(l + 12))
					emit(ic, ++number,
					     (ebr + le32(l + 8)) * 512,
					     (uint64_t) le32(l + 12) * 512, NULL, l[4]);
				if (!next[4] || !le32(next + 8))
					break;
				ebr = ext_base + le32(next + 8);
			}
		}
	}
}

/* Partition/filesystem scan of an image seen through @rd (file or callback). */
static int enum_reader(const w4_reader *rd, uint64_t size, const wchar_t *name,
		       e4_part_cb cb, void *ctx)
{
	struct img_ctx ic;
	unsigned char sec[512];
	e4_part whole;
	int i, has_gpt_marker = 0;

	memset(&ic, 0, sizeof(ic));
	ic.rd = *rd;
	ic.size = size;
	ic.path = name;
	ic.cb = cb;
	ic.ctx = ctx;

	/* A bare filesystem image (e.g. made with mkfs.ext4 file.img)? */
	memset(&whole, 0, sizeof(whole));
	probe(&ic.rd, 0, &whole);
	if (whole.is_ext) {
		emit(&ic, 0, 0, ic.size, NULL, 0);
		return 0;
	}

	if (!w4_read(&ic.rd, 0, sec, sizeof(sec)) &&
	    sec[510] == 0x55 && sec[511] == 0xAA) {
		for (i = 0; i < 4; i++)
			if (sec[446 + i * 16 + 4] == 0xEE)
				has_gpt_marker = 1;
		if (!has_gpt_marker || !(scan_gpt(&ic, 512) ||
					 scan_gpt(&ic, 4096))) {
			ic.table.part_style = 1;
			ic.table.sector_size = 512;
			sprintf(ic.table.disk_id, "%08X", le32(sec + 440));
			scan_mbr(&ic, sec);
		}
	}
	return ic.count || whole.is_ext ? 0 : E4_ENOTEXT;
}

E4_API int e4_enum_image(const wchar_t *path, e4_part_cb cb, void *ctx)
{
	LARGE_INTEGER sz;
	w4_reader rd = { INVALID_HANDLE_VALUE, 1, NULL };
	int ret;

	rd.h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
			   NULL, OPEN_EXISTING, 0, NULL);
	if (rd.h == INVALID_HANDLE_VALUE)
		return GetLastError() == ERROR_ACCESS_DENIED ? EACCES : ENOENT;
	if (!GetFileSizeEx(rd.h, &sz)) {
		CloseHandle(rd.h);
		return EIO;
	}
	ret = enum_reader(&rd, (uint64_t) sz.QuadPart, path, cb, ctx);
	CloseHandle(rd.h);
	return ret;
}

E4_API int e4_enum_cb(e4_read_cb read, void *read_ctx, uint64_t size,
		      const wchar_t *name, e4_part_cb cb, void *ctx)
{
	w4_reader rd = { INVALID_HANDLE_VALUE, 1, NULL };
	int ret = w4_dev_open_cb(read, read_ctx, size, 0, 0, &rd.dev);

	if (ret)
		return ret;
	ret = enum_reader(&rd, size, name, cb, ctx);
	w4_dev_close(rd.dev);
	return ret;
}
