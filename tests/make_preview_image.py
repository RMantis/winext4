"""Builds tests/work/preview-src (sample files for the preview tests); then run
   wsl mkfs.ext4 -q -F -d <src> tests/work/preview.img 32M   (done by preview_test.ps1)."""
import gzip
import os
import sys

from PIL import Image, ImageDraw

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
w = lambda name, data: open(os.path.join(out, name), "wb").write(data)

w("leggimi.txt", "Ciao! Questo è un file di testo UTF-8 con accenti: àèìòù €\nSeconda riga.\n".encode("utf-8"))
w("latin1.txt", "caffè e perché (Latin-1)\n".encode("latin-1"))
w("windows.txt", b"riga uno\r\nriga due\r\n")
w("config", b"# file di configurazione senza estensione\nkey=value\n")
w("grande.log", b"".join(f"{i:07d} riga di log abbastanza lunga per occupare spazio\n".encode() for i in range(60000)))
w("vuoto.txt", b"")
w("dati.gz", gzip.compress(b"x" * 1000))
w("binario.bin", bytes(range(256)) * 8)

img = Image.new("RGB", (1200, 800), (40, 110, 180))
d = ImageDraw.Draw(img)
d.ellipse([300, 150, 900, 650], fill=(250, 200, 60))
d.text((40, 40), "WinExt4 preview test", fill="white")
img.save(os.path.join(out, "foto.jpg"), quality=85)
img.resize((300, 200)).save(os.path.join(out, "logo.png"))
print("ok")
