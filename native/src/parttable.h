/*
 * Partition table helpers shared by scan.c and the editing API.
 */
#ifndef WINEXT4_PARTTABLE_H
#define WINEXT4_PARTTABLE_H

#include <windows.h>
#include <stdint.h>

uint32_t pt_crc32(const void *data, size_t len);

/* On-disk GUIDs (and Windows GUID structs) are little-endian mixed: */
void pt_format_guid(const unsigned char *g, char *out /* 37 bytes */);
int pt_parse_guid(const char *s, unsigned char *g /* 16 bytes */);

/* GPT name (UTF-16LE, 36 units) <-> UTF-8 */
void pt_name_to_utf8(const unsigned char *utf16le, char *out, int out_size);

/* E4_GPT_* flags for the GPT on @h (sector size @ssz, total size @size). */
uint32_t pt_verify_gpt(HANDLE h, uint32_t ssz, uint64_t size, uint32_t align);

#endif
