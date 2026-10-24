"""Wraps a raw disk image into a fixed-size VHD (raw data + 512-byte footer),
so Windows can attach it as a real disk with Mount-DiskImage.

    python tests/make_vhd.py tests/img/disk-gpt.img tests/work/vhd-gpt.vhd
"""
import os
import shutil
import struct
import sys
import time
import uuid


def geometry(size):
    total = min(size // 512, 65535 * 16 * 255)
    if total >= 65535 * 16 * 63:
        spt, heads = 255, 16
        cth = total // spt
    else:
        spt = 17
        cth = total // spt
        heads = max((cth + 1023) // 1024, 4)
        if cth >= heads * 1024 or heads > 16:
            spt, heads = 31, 16
            cth = total // spt
        if cth >= heads * 1024:
            spt, heads = 63, 16
            cth = total // spt
    return struct.pack(">HBB", cth // heads, heads, spt)


def footer(size):
    f = bytearray(512)
    struct.pack_into(">8sIIQI4sI4sQQ4sI", f, 0,
                     b"conectix", 2, 0x00010000, 0xFFFFFFFFFFFFFFFF,
                     int(time.time()) - 946684800, b"wx4 ", 0x00010000, b"Wi2k",
                     size, size, geometry(size), 2)
    f[68:84] = uuid.uuid4().bytes
    checksum = ~sum(f) & 0xFFFFFFFF
    struct.pack_into(">I", f, 64, checksum)
    return bytes(f)


src, dst = sys.argv[1], sys.argv[2]
size = os.path.getsize(src)
assert size % 512 == 0
shutil.copyfile(src, dst)
with open(dst, "ab") as out:
    out.write(footer(size))
print(f"{dst}: {size} bytes + footer")
