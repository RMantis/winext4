#!/bin/bash
# Draft (overlay) mode end to end with e4cli: the source must not change
# until the draft is applied; verified with e2fsck/debugfs in WSL.
set -u
export MSYS_NO_PATHCONV=1
ROOT=$(cd "$(dirname "$0")/.." && pwd)
C="$ROOT/native/build/Release/e4cli.exe"
W="$ROOT/tests/work"
WL=$(cygpath -u "$ROOT" | sed 's#^/\([a-z]\)/#/mnt/\1/#')/tests/work
fails=0
ok()  { echo "  ok   $*"; }
bad() { echo "  FAIL $*"; fails=$((fails + 1)); }
win() { cygpath -w "$1"; }

cp "$ROOT/tests/img/ext4.img" "$W/d.img"
rm -f "$W/d.wx4draft"
IMG=$(win "$W/d.img"); D=$(win "$W/d.wx4draft")
before=$(sha256sum < "$W/d.img")
head -c 3000000 /dev/urandom > "$W/draftfile.bin"

echo "== edits go to the draft"
E4DRAFT="$D" "$C" put "$IMG" 0 "$(win "$W/draftfile.bin")" / nuovo.bin || bad "put"
E4DRAFT="$D" "$C" mkdir "$IMG" 0 / cartella-bozza || bad "mkdir"
E4DRAFT="$D" "$C" mv "$IMG" 0 / hello.txt /cartella-bozza hello.txt || bad "mv"
E4DRAFT="$D" "$C" rm "$IMG" 0 /dir many_1 || bad "rm"
[ "$(sha256sum < "$W/d.img")" = "$before" ] && ok "source image unchanged" || bad "source changed"
a=$(E4DRAFT="$D" "$C" cat "$IMG" 0 /nuovo.bin | sha256sum); b=$(sha256sum < "$W/draftfile.bin")
[ "$a" = "$b" ] && ok "new file readable through the draft" || bad "draft content"
"$C" cat "$IMG" 0 /hello.txt | grep -q "hello from ext4" && ok "source still shows the old tree" || bad "source view"
"$C" draftinfo "$D"

echo "== crash recovery: garbage after the last commit is dropped"
cp "$W/d.wx4draft" "$W/d.backup"
head -c 3000 /dev/urandom >> "$W/d.wx4draft"
n1=$("$C" draftinfo "$D" | sed 's/.*chunks=\([0-9]*\).*/\1/')
n0=$("$C" draftinfo "$(win "$W/d.backup")" | sed 's/.*chunks=\([0-9]*\).*/\1/')
[ "$n1" = "$n0" ] && ok "garbage tail ignored ($n1 chunks)" || bad "garbage changed the draft: $n0 -> $n1"
a=$(E4DRAFT="$D" "$C" cat "$IMG" 0 /cartella-bozza/hello.txt)
[ "$a" = "hello from ext4" ] && ok "draft reopens after a torn write" || bad "torn write: $a"
fb=$("$C" draftinfo "$D" | sed 's/.*file=\([0-9]*\).*/\1/')
[ "$fb" = "$(stat -c %s "$W/d.wx4draft")" ] && ok "only valid records remain ($fb bytes)" || bad "tail not cleaned: $fb vs $(stat -c %s "$W/d.wx4draft")"

echo "== fingerprint"
"$C" draftcheck "$IMG" 0 "$D" && ok "draft matches its source" || bad "draftcheck"
cp "$W/d.img" "$W/d2.img"
"$C" mkdir "$(win "$W/d2.img")" 0 / altro >/dev/null
"$C" draftcheck "$(win "$W/d2.img")" 0 "$D" 2>/dev/null && bad "changed source accepted" || ok "changed source refused"
E4DRAFT="$D" "$C" ls "$(win "$W/d2.img")" 0 / >/dev/null 2>&1 && bad "draft opened on changed source" || ok "draft not opened on a changed source"

echo "== apply"
"$C" draftapply "$IMG" 0 "$D" && ok "applied" || bad "apply"
wsl -d Debian -- e2fsck -fn "$WL/d.img" >/dev/null 2>&1 && ok "e2fsck clean after apply" || bad "e2fsck after apply"
a=$(wsl -d Debian -- debugfs -R "cat /nuovo.bin" "$WL/d.img" 2>/dev/null | sha256sum)
[ "$a" = "$b" ] && ok "applied content verified by debugfs" || bad "applied content"
wsl -d Debian -- debugfs -R "ls /cartella-bozza" "$WL/d.img" 2>/dev/null | grep -q hello.txt && ok "moved file present" || bad "moved file"
"$C" draftinfo "$D" | grep -q "apply_state=2" && ok "draft marked applied" || bad "apply state"
E4DRAFT="$D" "$C" ls "$IMG" 0 / >/dev/null 2>&1 && bad "applied draft reopened" || ok "applied draft cannot be reopened"
"$C" draftapply "$IMG" 0 "$D" && ok "apply is idempotent (re-run)" || bad "re-apply"
wsl -d Debian -- e2fsck -fn "$WL/d.img" >/dev/null 2>&1 && ok "e2fsck clean after re-apply" || bad "e2fsck after re-apply"

echo "== journal replay happens inside the draft"
cp "$W/dirty.img" "$W/dirty-src.img" 2>/dev/null || MSYS_NO_PATHCONV=1 wsl -d Debian -- sh "$(cygpath -u "$ROOT" | sed 's#^/\([a-z]\)/#/mnt/\1/#')/tests/make_dirty_journal.sh" >/dev/null
[ -f "$W/dirty-src.img" ] || cp "$W/dirty.img" "$W/dirty-src.img"
rm -f "$W/dirty.wx4draft"
dsum=$(sha256sum < "$W/dirty-src.img")
a=$(E4DRAFT="$(win "$W/dirty.wx4draft")" "$C" cat "$(win "$W/dirty-src.img")" 0 /hello.txt | head -c 16)
[ "$a" = "REPLAYED CONTENT" ] && ok "journal replayed in the draft" || bad "replay: $a"
[ "$(sha256sum < "$W/dirty-src.img")" = "$dsum" ] && ok "dirty source untouched" || bad "dirty source modified"

echo
[ $fails -eq 0 ] && echo "ALL DRAFT TESTS PASSED" || echo "$fails FAILURES"
exit $fails
