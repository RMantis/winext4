#!/bin/bash
# End-to-end test of ext4core: modifies copies of the test images with
# e4cli.exe (Windows) and checks the result with the real e2fsprogs in WSL.
#
#   bash tests/run_tests.sh            (from Git Bash, after make_images.sh)
set -u
export MSYS_NO_PATHCONV=1

ROOT=$(cd "$(dirname "$0")/.." && pwd)
CLI="$ROOT/native/build/Release/e4cli.exe"
IMG="$ROOT/tests/img"
WORK="$ROOT/tests/work"
WROOT=$(cygpath -u "$ROOT" | sed 's#^/\([a-z]\)/#/mnt/\1/#')
DISTRO=Debian
fails=0

mkdir -p "$WORK"

ok()   { echo "  ok   $*"; }
bad()  { echo "  FAIL $*"; fails=$((fails + 1)); }
e4()   { "$CLI" "$@"; }
wsl_() { wsl -d "$DISTRO" -- "$@"; }
win()  { cygpath -w "$1"; }

# md5 of a file inside the image according to Linux debugfs
dbg_md5() { wsl_ sh -c "debugfs -R 'cat $2' '$1' 2>/dev/null | md5sum | cut -c1-32"; }
# md5 of a file inside the image according to ext4core
e4_md5() { e4 cat "$(win "$1")" "$2" "$3" | md5sum | cut -c1-32; }

check_fsck() {
	local out
	out=$(wsl_ e2fsck -fn "$1" 2>&1)
	if [ $? -eq 0 ]; then ok "e2fsck clean"; else bad "e2fsck: $out"; fi
}

head -c 5000000 /dev/urandom > "$WORK/big.bin"
head -c 70000 /dev/urandom > "$WORK/mid.bin"
echo "small file" > "$WORK/small.txt"
BIG_MD5=$(md5sum < "$WORK/big.bin" | cut -c1-32)
MID_MD5=$(md5sum < "$WORK/mid.bin" | cut -c1-32)

for name in ext4 ext4-inline ext3 ext2 ext4-1k; do
	echo "== $name"
	cp "$IMG/$name.img" "$WORK/$name.img"
	W="$WORK/$name.img"
	L="$WROOT/tests/work/$name.img"
	WW=$(win "$W")

	# --- reading
	a=$(e4_md5 "$W" 0 /dir/random.bin); b=$(dbg_md5 "$L" /dir/random.bin)
	[ "$a" = "$b" ] && ok "read random.bin" || bad "read random.bin $a != $b"
	a=$(e4 cat "$WW" 0 "/dir/città.txt" | tr -d '\r')
	[ "$a" = "unicode: àèìòù €" ] && ok "read utf-8 name" || bad "utf-8 name: $a"
	n=$(e4 ls "$WW" 0 /dir | wc -l)
	[ "$n" -eq 505 ] && ok "readdir 505 entries" || bad "readdir count $n"

	# --- writing
	e4 put "$WW" 0 "$(win "$WORK/big.bin")" /dir new.bin || bad "put big"
	e4 mkdir "$WW" 0 / newdir || bad "mkdir"
	e4 put "$WW" 0 "$(win "$WORK/small.txt")" /newdir "nuovo è.txt" || bad "put utf8"
	e4 mv "$WW" 0 / hello.txt /newdir hello2.txt || bad "mv file"
	for i in $(seq 1 60); do e4 rm "$WW" 0 /dir "many_$i" || bad "rm many_$i"; done
	e4 rmdir "$WW" 0 / empty || bad "rmdir"
	e4 ln "$WW" 0 /newdir link-to-hello hello2.txt || bad "symlink"
	e4 trunc "$WW" 0 /dir/random.bin 1000000 || bad "truncate"
	e4 mv "$WW" 0 / dir /newdir dir_moved || bad "mv dir"
	e4 put "$WW" 0 "$(win "$WORK/mid.bin")" /newdir/dir_moved new.bin || bad "overwrite"
	e4 mkdir "$WW" 0 /newdir deep || bad "mkdir 2"
	e4 mv "$WW" 0 / newdir /newdir/dir_moved inside 2>/dev/null && bad "mv into own child allowed" || ok "mv into own child refused"
	e4 mv "$WW" 0 /newdir deep /newdir/dir_moved/sub deep || bad "mv dir sideways"
	e4 rmdir "$WW" 0 /newdir dir_moved 2>/dev/null && bad "rmdir non-empty allowed" || ok "rmdir non-empty refused"
	e4 mkdir "$WW" 0 / newdir 2>/dev/null && bad "mkdir duplicate allowed" || ok "mkdir duplicate refused"

	check_fsck "$L"

	a=$(dbg_md5 "$L" /newdir/dir_moved/new.bin)
	[ "$a" = "$MID_MD5" ] && ok "overwritten content" || bad "overwrite md5 $a"
	a=$(wsl_ debugfs -R "cat /newdir/hello2.txt" "$L" 2>/dev/null)
	[ "$a" = "hello from ext4" ] && ok "renamed file content" || bad "renamed: $a"
	a=$(wsl_ debugfs -R "stat /newdir/link-to-hello" "$L" 2>/dev/null | grep -c 'link dest: "hello2.txt"')
	[ "$a" = "1" ] && ok "symlink target" || bad "symlink target"
	a=$(e4 ls "$WW" 0 /newdir | grep -c link-to-hello)
	[ "$a" = "1" ] && ok "symlink listed" || bad "symlink listed"
	a=$(wsl_ debugfs -R "stat /newdir/dir_moved/random.bin" "$L" 2>/dev/null | grep -o "Size: [0-9]*" | head -1)
	[ "$a" = "Size: 1000000" ] && ok "truncate size" || bad "truncate: $a"
	a=$(wsl_ debugfs -R "ls -l /newdir/dir_moved/sub" "$L" 2>/dev/null | grep -c inner.txt)
	[ "$a" = "1" ] && ok "moved subtree" || bad "moved subtree"
	a=$(wsl_ debugfs -R "stat /newdir/dir_moved" "$L" 2>/dev/null | grep -c "Links: 3")
	[ "$a" = "1" ] && ok "dir link count" || bad "dir link count"
	a=$(e4 cat "$WW" 0 "/newdir/nuovo è.txt")
	[ "$a" = "small file" ] && ok "utf-8 create" || bad "utf-8 create: $a"

	# big file round trip after reopen
	e4 put "$WW" 0 "$(win "$WORK/big.bin")" /newdir big.bin || bad "put big 2"
	a=$(e4_md5 "$W" 0 /newdir/big.bin)
	[ "$a" = "$BIG_MD5" ] && ok "big round trip" || bad "big md5 $a"
	e4 rm "$WW" 0 /newdir big.bin || bad "rm big"
	check_fsck "$L"
done

# --- partitioned disk images
for name in disk-mbr disk-gpt; do
	echo "== $name"
	out=$(e4 scan "$(win "$IMG/$name.img")")
	off=$(echo "$out" | grep "ext=1" | sed 's/.*offset=\([0-9]*\).*/\1/')
	[ -n "$off" ] && ok "found ext partition at $off" || { bad "scan: $out"; continue; }
	a=$(e4 cat "$(win "$IMG/$name.img")" "$off" /hello.txt)
	[ "$a" = "hello from ext4" ] && ok "read from partition" || bad "partition read: $a"
done

echo
if [ $fails -eq 0 ]; then echo "ALL TESTS PASSED"; else echo "$fails FAILURES"; fi
exit $fails
