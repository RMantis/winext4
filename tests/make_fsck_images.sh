#!/bin/sh
# Damaged copies of tests/img/ext4.img for the fsck tests (run in WSL):
#   broken.img  several inconsistencies that need answers (-y)
#   preen.img   only what e2fsck -p fixes on its own (summary counters)
set -e
D=$(cd "$(dirname "$0")" && pwd)
W=$D/work
mkdir -p "$W"

cp "$D/img/ext4.img" "$W/broken.img"
debugfs -w "$W/broken.img" >/dev/null 2>&1 <<'EOF'
sif /hello.txt links_count 5
clri /dir/many_3
unlink /dir/sub
freeb 2000 20
ssv free_inodes_count 1
EOF

cp "$D/img/ext4.img" "$W/preen.img"
debugfs -w "$W/preen.img" >/dev/null 2>&1 <<'EOF'
ssv free_blocks_count 12
ssv free_inodes_count 34
EOF
ls -la "$W/broken.img" "$W/preen.img"
