/*
 * Copy-on-write overlay ("draft"): writes go to a delta file, the source
 * is never modified until the draft is applied.
 */
#ifndef WINEXT4_OVERLAY_H
#define WINEXT4_OVERLAY_H

#include "winio.h"

#define OV_CHUNK 4096

typedef struct w4_overlay w4_overlay;

/*
 * Opens (or creates) @delta on top of @base. An existing delta must have
 * been made from the same source (fingerprint), else E4_EDRAFT_MISMATCH.
 * Records written after the last commit (a crash) are dropped.
 */
int ov_open(const wchar_t *delta, w4_dev *base, const wchar_t *desc, w4_overlay **out);
errcode_t ov_read(w4_overlay *ov, uint64_t pos, void *buf, size_t len);
errcode_t ov_write(w4_overlay *ov, uint64_t pos, const void *buf, size_t len);
errcode_t ov_commit(w4_overlay *ov);
void ov_close(w4_overlay *ov);

size_t ov_count(w4_overlay *ov);

/* SHA-512 of the first 128 KB of @base and its length. */
int ov_fingerprint(w4_dev *base, unsigned char out[64]);
/* 0 if @delta was made from @base, E4_EDRAFT_MISMATCH otherwise. */
int ov_check(const wchar_t *delta, w4_dev *base);
/* Writes the draft into @target (opened for writing). */
int ov_apply(const wchar_t *delta, w4_dev *target, volatile int32_t *cancel,
	     volatile uint64_t *progress);

#endif
