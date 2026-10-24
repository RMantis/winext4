#!/bin/sh
# Makes tests/work/dirty.img: an ext4 image whose journal holds a committed
# but unreplayed transaction that rewrites the data block of /hello.txt,
# like a disk unplugged from Linux without unmounting.
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
IMG=$ROOT/tests/work/dirty.img
cp "$ROOT/tests/img/ext4.img" "$IMG"
BLK=$(debugfs -R "bmap /hello.txt 0" "$IMG" 2>/dev/null)
BS=$(dumpe2fs -h "$IMG" 2>/dev/null | sed -n 's/^Block size: *//p')
printf 'REPLAYED CONTENT\n' > /tmp/blk
truncate -s "$BS" /tmp/blk
printf 'jo\njw -b %s /tmp/blk\njc\n' "$BLK" > /tmp/cmds
debugfs -w -f /tmp/cmds "$IMG" >/dev/null 2>&1
rm -f /tmp/blk /tmp/cmds
echo "block=$BLK bs=$BS"
dumpe2fs -h "$IMG" 2>/dev/null | grep -i "features"
