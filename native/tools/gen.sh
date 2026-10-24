#!/bin/sh
# Generates the files that e2fsprogs' configure/make would normally produce.
# Run from WSL (or any Unix shell with awk + cc): sh native/tools/gen.sh
set -e
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
E=$ROOT/third_party/e2fsprogs
G=$ROOT/native/generated
mkdir -p "$G"
sed -e "s/@E2FSPROGS_VERSION@/1.47.4/" "$E/lib/ext2fs/ext2_err.et.in" > "$G/ext2_err.et"
mkdir -p "$G/ext2fs"
cd "$G"
awk -f "$E/lib/et/et_h.awk" outfile=ext2fs/ext2_err.h outfn=ext2_err.h ext2_err.et
awk -f "$E/lib/et/et_c.awk" outfile=ext2_err.c outfn=ext2_err.c ext2_err.et
rm -f ext2_err.et
cc -I"$E/lib" -I"$E/lib/ext2fs" -o /tmp/gen_crc32ctable "$E/lib/ext2fs/gen_crc32ctable.c"
/tmp/gen_crc32ctable > crc32c_table.h
rm -f /tmp/gen_crc32ctable
ls -la "$G"

# lib/support error table (e4fsck: e2fsck profile support)
mkdir -p "$G/support"
cd "$G/support"
awk -f "$E/lib/et/et_h.awk" outfile=prof_err.h outfn=prof_err.h "$E/lib/support/prof_err.et"
awk -f "$E/lib/et/et_c.awk" outfile=prof_err.c outfn=prof_err.c "$E/lib/support/prof_err.et"
