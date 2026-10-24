#!/bin/bash
# e4fsck (e2fsck on WinExt4's I/O) against Linux e2fsck in WSL: same
# findings, repairs that Linux then sees as clean, repairs into a draft.
set -u
export MSYS_NO_PATHCONV=1
ROOT=$(cd "$(dirname "$0")/.." && pwd)
F="$ROOT/native/build/Release/e4fsck.exe"
C="$ROOT/native/build/Release/e4cli.exe"
W="$ROOT/tests/work"
WL=$(cygpath -u "$ROOT" | sed 's#^/\([a-z]\)/#/mnt/\1/#')/tests/work
fails=0
ok()  { echo "  ok   $*"; }
bad() { echo "  FAIL $*"; fails=$((fails + 1)); }
win() { cygpath -w "$1"; }
lfsck() { wsl -d Debian -- e2fsck "$@"; }
# the findings, without version banner and device-specific lines
findings() { grep -E "^(Entry|Unconnected|Inode|Block bitmap|Inode bitmap|Free|'\.\.')" | sed 's/(was in \/dir)/(\/dir\/???)/'; }

wsl -d Debian -- sh "$(cygpath -u "$ROOT" | sed 's#^/\([a-z]\)/#/mnt/\1/#')/tests/make_fsck_images.sh" >/dev/null

echo "== clean filesystem"
cp "$ROOT/tests/img/ext4.img" "$W/fclean.img"
"$F" -fn "$(win "$W/fclean.img")" >/dev/null 2>&1; rc=$?
[ $rc -eq 0 ] && ok "clean image: exit 0" || bad "clean image: exit $rc"

echo "== check only (-n)"
before=$(sha256sum < "$W/broken.img")
"$F" -fn "$(win "$W/broken.img")" > "$W/e4.out" 2>&1; rc=$?
[ $rc -eq 4 ] && ok "errors found: exit 4" || bad "check: exit $rc"
[ "$(sha256sum < "$W/broken.img")" = "$before" ] && ok "image untouched by -n" || bad "-n modified the image"
lfsck -fn "$WL/broken.img" > "$W/linux.out" 2>&1
if diff <(findings < "$W/e4.out") <(findings < "$W/linux.out") >/dev/null; then
    ok "same findings as Linux e2fsck ($(findings < "$W/e4.out" | wc -l) lines)"
else
    bad "findings differ from Linux"; diff <(findings < "$W/e4.out") <(findings < "$W/linux.out")
fi

echo "== automatic repair (-p) refuses what needs answers"
cp "$W/broken.img" "$W/bp.img"
"$F" -fp "$(win "$W/bp.img")" > "$W/p.out" 2>&1; rc=$?
[ $rc -eq 4 ] && ok "preen stops: exit 4" || bad "preen on broken: exit $rc"
grep -q "UNEXPECTED INCONSISTENCY" "$W/p.out" && ok "asks to run manually" || bad "no manual-run message"

echo "== automatic repair (-p) of safe problems"
"$F" -fp "$(win "$W/preen.img")" > "$W/p2.out" 2>&1; rc=$?
[ $rc -eq 1 ] && ok "preen fixed: exit 1" || { bad "preen: exit $rc"; cat "$W/p2.out"; }
lfsck -fn "$WL/preen.img" >/dev/null 2>&1 && ok "Linux e2fsck: clean after preen" || bad "not clean after preen"

echo "== full repair (-y)"
cp "$W/broken.img" "$W/by.img"
"$F" -fy "$(win "$W/by.img")" > "$W/y.out" 2>&1; rc=$?
[ $rc -eq 1 ] && ok "repaired: exit 1" || { bad "repair: exit $rc"; tail "$W/y.out"; }
lfsck -fn "$WL/by.img" >/dev/null 2>&1 && ok "Linux e2fsck: clean after repair" || bad "not clean after repair"
"$F" -fn "$(win "$W/by.img")" >/dev/null 2>&1 && ok "e4fsck: clean after repair" || bad "e4fsck still finds errors"
wsl -d Debian -- debugfs -R "ls /lost+found" "$WL/by.img" 2>/dev/null | grep -q "#519" && ok "lost directory in lost+found" || bad "lost+found"
a=$(wsl -d Debian -- debugfs -R "cat /lost+found/#519/inner.txt" "$WL/by.img" 2>/dev/null)
b=$(wsl -d Debian -- debugfs -R "cat /dir/sub/inner.txt" "$WL/fclean.img" 2>/dev/null)
[ -n "$a" ] && [ "$a" = "$b" ] && ok "its content survived" || bad "lost content: '$a'"

echo "== repair into a draft"
rm -f "$W/fsck.wx4draft"
"$F" -fy --draft "$(win "$W/fsck.wx4draft")" --desc "test" "$(win "$W/broken.img")" > "$W/d.out" 2>&1; rc=$?
[ $rc -eq 1 ] && ok "repaired in the draft: exit 1" || { bad "draft repair: exit $rc"; tail "$W/d.out"; }
[ "$(sha256sum < "$W/broken.img")" = "$before" ] && ok "source untouched" || bad "source modified"
"$F" -fn --draft "$(win "$W/fsck.wx4draft")" "$(win "$W/broken.img")" >/dev/null 2>&1 \
    && ok "the draft view is clean" || bad "draft still has errors"
cp "$W/broken.img" "$W/bd.img"
"$C" draftapply "$(win "$W/bd.img")" 0 "$(win "$W/fsck.wx4draft")" >/dev/null && ok "draft applied" || bad "apply"
lfsck -fn "$WL/bd.img" >/dev/null 2>&1 && ok "Linux e2fsck: clean after applying the draft" || bad "not clean after apply"
cmp -s "$W/bd.img" "$W/by.img" && ok "same result as repairing in place" || echo "  note different bytes from the in-place repair (timestamps)"

echo "== dirty journal"
[ -f "$W/dirty.img" ] || wsl -d Debian -- sh "$(cygpath -u "$ROOT" | sed 's#^/\([a-z]\)/#/mnt/\1/#')/tests/make_dirty_journal.sh" >/dev/null
cp "$W/dirty.img" "$W/fdirty.img"
"$F" -fy "$(win "$W/fdirty.img")" > "$W/j.out" 2>&1; rc=$?
[ $rc -le 1 ] && ok "journal recovered: exit $rc" || { bad "journal: exit $rc"; tail "$W/j.out"; }
a=$(wsl -d Debian -- debugfs -R "cat /hello.txt" "$WL/fdirty.img" 2>/dev/null | head -c 16)
[ "$a" = "REPLAYED CONTENT" ] && ok "replayed content present" || bad "replay: $a"
lfsck -fn "$WL/fdirty.img" >/dev/null 2>&1 && ok "Linux e2fsck: clean" || bad "not clean after journal recovery"

echo "== progress"
"$F" -fn -C 2 "$(win "$W/fclean.img")" 2> "$W/c.err" >/dev/null
n=$(grep -cE "^[1-5] [0-9]+ [0-9]+ " "$W/c.err")
[ "$n" -gt 5 ] && ok "progress lines on stderr ($n)" || bad "progress: $n lines"

echo "== refusals"
"$F" -fy --draft "$(win "$W/fsck.wx4draft")" "$(win "$W/fclean.img")" >/dev/null 2>"$W/r.err"; rc=$?
[ $rc -eq 8 ] && grep -q "different or changed source" "$W/r.err" && ok "draft of another source refused" || bad "draft mismatch: exit $rc"

echo
[ $fails -eq 0 ] && echo "ALL FSCK TESTS PASSED" || echo "$fails FAILURES"
exit $fails
