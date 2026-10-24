"""Builds archives containing the test images, in every supported layout.

    python tests/make_archives.py <img dir> <out dir>

xz files are made by the caller with WSL's xz (multi-block and single-block).
"""
import gzip
import io
import os
import sys
import tarfile
import zipfile

import zstandard

src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
ext4 = open(os.path.join(src, "ext4.img"), "rb").read()
gpt = open(os.path.join(src, "disk-gpt.img"), "rb").read()


def w(name, data):
    with open(os.path.join(out, name), "wb") as f:
        f.write(data)


# tar with a small file, a GPT disk image and an ext4 filesystem image
buf = io.BytesIO()
with tarfile.open(fileobj=buf, mode="w", format=tarfile.GNU_FORMAT) as t:
    for name, data in [("readme.txt", b"ciao\n"), ("immagini/disco-gpt.img", gpt),
                       ("immagini/un-nome-di-file-molto-lungo-per-provare-le-intestazioni-gnu-longname-della-libreria-tar.img", ext4)]:
        ti = tarfile.TarInfo(name)
        ti.size = len(data)
        t.addfile(ti, io.BytesIO(data))
tar = buf.getvalue()
w("immagini.tar", tar)
w("immagini.tar.gz", gzip.compress(tar, 6))
w("ext4.img.gz", gzip.compress(ext4, 9))
# multi-member gzip (like pigz --independent or concatenated .gz files)
w("multi.img.gz", gzip.compress(ext4[:20_000_000]) + gzip.compress(ext4[20_000_000:]))

# zstd: several frames with content size (random access) and one big frame (cache)
cctx = zstandard.ZstdCompressor(level=3, write_content_size=True)
frames = b"".join(cctx.compress(gpt[i:i + (8 << 20)]) for i in range(0, len(gpt), 8 << 20))
w("disco-gpt.img.zst", frames)
w("singolo.img.zst", zstandard.ZstdCompressor(level=3).compress(ext4))
w("immagini.tar.zst", cctx.compress(tar))

# zip: one stored member, one deflated
with zipfile.ZipFile(os.path.join(out, "immagini.zip"), "w") as z:
    z.writestr(zipfile.ZipInfo("salvato/ext4.img"), ext4, compress_type=zipfile.ZIP_STORED)
    z.writestr(zipfile.ZipInfo("compresso/disco-gpt.img"), gpt, compress_type=zipfile.ZIP_DEFLATED)
print("ok")
