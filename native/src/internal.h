/*
 * Accessors shared between ext4core.c and the other API files.
 */
#ifndef WINEXT4_INTERNAL_H
#define WINEXT4_INTERNAL_H

#include "ext2fs/ext2fs.h"
#include "ext4core.h"

void e4_lock(e4_fs *f);
void e4_unlock(e4_fs *f);
ext2_filsys e4_filsys(e4_fs *f);
int e4_writable(e4_fs *f);
int e4_xlate(errcode_t err);

#endif
