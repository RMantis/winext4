#!/bin/sh
# Creates ext test images in tests/img (run from WSL: sh tests/make_images.sh).
# Uses mkfs -d to populate the images without needing root/loop mounts.
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=$ROOT/tests/img
SRC=$(mktemp -d)
mkdir -p "$OUT"

mkdir -p "$SRC/dir/sub" "$SRC/empty" "$SRC/Case" "$SRC/case"
echo "hello from ext4" > "$SRC/hello.txt"
printf 'unicode: àèìòù €\n' > "$SRC/dir/città.txt"
head -c 3000000 /dev/urandom > "$SRC/dir/random.bin"
echo inner > "$SRC/dir/sub/inner.txt"
echo upper > "$SRC/Case/a.txt"
echo lower > "$SRC/case/a.txt"
echo 'x' > "$SRC/name:with:colons.txt"
ln -s hello.txt "$SRC/link-rel"
ln -s /etc/passwd "$SRC/link-abs"
ln -s "$(printf 'a%.0s' $(seq 1 100))" "$SRC/link-long"
chmod 600 "$SRC/hello.txt"
for i in $(seq 1 500); do echo "$i" > "$SRC/dir/many_$i"; done

rm -f "$OUT"/*.img
mkfs.ext4 -q -F -I 256 -L TEST4 -d "$SRC" "$OUT/ext4.img" 64M
mkfs.ext4 -q -F -I 256 -L META -O metadata_csum,inline_data -d "$SRC" "$OUT/ext4-inline.img" 64M
mkfs.ext3 -q -F -L TEST3 -d "$SRC" "$OUT/ext3.img" 32M
mkfs.ext2 -q -F -L TEST2 -d "$SRC" "$OUT/ext2.img" 32M
mkfs.ext4 -q -F -b 1024 -L SMALLBS -d "$SRC" "$OUT/ext4-1k.img" 32M

# Disk image with an MBR: partition 1 = FAT-ish filler, partition 2 = ext4
truncate -s 80M "$OUT/disk-mbr.img"
printf 'label: dos\nstart=2048, size=16384, type=c\nstart=20480, size=131072, type=83\n' | sfdisk -q "$OUT/disk-mbr.img"
dd if="$OUT/ext4.img" of="$OUT/disk-mbr.img" bs=1M seek=$((20480 * 512)) oflag=seek_bytes conv=notrunc status=none

# Disk image with GPT
truncate -s 80M "$OUT/disk-gpt.img"
printf 'label: gpt\nstart=2048, size=131072, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4\n' | sfdisk -q "$OUT/disk-gpt.img"
dd if="$OUT/ext4.img" of="$OUT/disk-gpt.img" bs=1M seek=$((2048 * 512)) oflag=seek_bytes conv=notrunc status=none

rm -rf "$SRC"
ls -la "$OUT"
