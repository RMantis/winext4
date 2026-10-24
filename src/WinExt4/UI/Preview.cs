using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using WinExt4.Core;

namespace WinExt4.UI;

public enum PreviewKind { None, Text, Image, Hex, Directory, Message }

/// <summary>Everything needed to show a preview; built off the UI thread (images are frozen).</summary>
public sealed class PreviewData
{
    public PreviewKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string? Text { get; init; }
    public BitmapSource? Image { get; init; }
    public string? Note { get; init; }
}

/// <summary>Reads just what is needed straight from the ext volume (no temp files).</summary>
public static class PreviewBuilder
{
    public const int TextLimit = 2 * 1024 * 1024;
    public const long ImageLimit = 64L * 1024 * 1024;
    private const int HexBytes = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static PreviewData Build(Ext4Volume vol, EntryItem item, int decodeWidth, CancellationToken cancel)
    {
        string title = item.Entry.Name;
        var st = item.Stat;
        uint ino = item.Entry.Ino;
        string linkNote = "";
        if (item.IsLink)
        {
            linkNote = $"Link simbolico → {item.LinkTarget}";
            if (item.TargetStat is null)
                return new PreviewData { Kind = PreviewKind.Message, Title = title, Subtitle = linkNote, Text = "Il link non punta a nessun file esistente in questo filesystem." };
            st = item.TargetStat;
            ino = item.TargetIno;
        }

        if (st.IsDirectory)
        {
            int count = vol.ReadDir(ino).Count;
            return new PreviewData
            {
                Kind = PreviewKind.Directory,
                Title = title,
                Subtitle = Join(linkNote, "Cartella"),
                Text = count == 0 ? "Cartella vuota" : $"{count} elementi",
            };
        }
        if (!st.IsRegular)
            return new PreviewData { Kind = PreviewKind.Message, Title = title, Subtitle = Join(linkNote, item.TypeText), Text = "Nessuna anteprima per i file speciali." };
        if (st.Size == 0)
            return new PreviewData { Kind = PreviewKind.Message, Title = title, Subtitle = Join(linkNote, "0 byte"), Text = "File vuoto." };

        string size = Format.Size(st.Size);
        byte[] head = ReadRange(vol, ino, 0, (int)Math.Min(st.Size, 64 * 1024), cancel);
        string? magic = Magic.Describe(head);

        if (Magic.IsImage(head) && st.Size <= ImageLimit)
        {
            byte[] all = head.Length == (long)st.Size ? head : ReadRange(vol, ino, 0, (int)st.Size, cancel);
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = new MemoryStream(all);
                bmp.EndInit();
                bmp.Freeze();
                // pixel size of the original, not of the decoded thumbnail
                var frame = BitmapFrame.Create(new MemoryStream(all), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                return new PreviewData
                {
                    Kind = PreviewKind.Image,
                    Title = title,
                    Subtitle = Join(linkNote, $"{magic} · {frame.PixelWidth}×{frame.PixelHeight} · {size}"),
                    Image = bmp,
                };
            }
            catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
            {
                // No WIC codec (e.g. HEIC without the extension): fall through to hex.
                magic = (magic ?? "Immagine") + " (formato non supportato da questo Windows)";
            }
        }

        if (magic is null && LooksLikeText(head, out var encoding))
        {
            byte[] text = st.Size <= (ulong)head.Length ? head : ReadRange(vol, ino, 0, (int)Math.Min(st.Size, (ulong)TextLimit), cancel);
            string s = Decode(text, encoding);
            bool truncated = st.Size > (ulong)text.Length;
            string eol = s.Contains("\r\n") ? "CRLF" : s.Contains('\n') ? "LF" : "";
            return new PreviewData
            {
                Kind = PreviewKind.Text,
                Title = title,
                Subtitle = Join(linkNote, $"Testo {encoding.WebName.ToUpperInvariant()}{(eol.Length > 0 ? " · " + eol : "")} · {size}"),
                Text = s,
                Note = truncated ? $"File troncato: mostrati i primi {Format.Size((ulong)text.Length)} di {size}." : null,
            };
        }

        return new PreviewData
        {
            Kind = PreviewKind.Hex,
            Title = title,
            Subtitle = Join(linkNote, $"{magic ?? "Dati binari"} · {size}"),
            Text = HexDump(head.AsSpan(0, Math.Min(head.Length, HexBytes))),
            Note = st.Size > HexBytes ? $"Primi {Format.Size(HexBytes)} di {size}." : null,
        };
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : $"{a} · {b}";

    private static byte[] ReadRange(Ext4Volume vol, uint ino, ulong offset, int length, CancellationToken cancel)
    {
        var buf = new byte[length];
        int done = 0;
        while (done < length)
        {
            cancel.ThrowIfCancellationRequested();
            int n = vol.Read(ino, offset + (ulong)done, buf.AsSpan(done, Math.Min(1 << 20, length - done)));
            if (n <= 0) break;
            done += n;
        }
        return done == length ? buf : buf[..done];
    }

    /// <summary>Text if there are no NULs and it decodes as UTF-8 (or is plausible Latin-1).</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> data, out Encoding encoding)
    {
        encoding = Encoding.UTF8;
        var sample = data[..Math.Min(data.Length, 64 * 1024)];
        if (sample.IndexOf((byte)0) >= 0)
        {
            // UTF-16 with BOM is text too
            if (sample.Length >= 2 && (sample[0] == 0xFF && sample[1] == 0xFE || sample[0] == 0xFE && sample[1] == 0xFF))
            {
                encoding = sample[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
                return true;
            }
            return false;
        }
        // Cut at the last complete UTF-8 sequence before validating a partial read.
        int end = sample.Length;
        while (end > 0 && end > sample.Length - 4 && (sample[end - 1] & 0xC0) == 0x80) end--;
        if (end > 0 && sample[end - 1] >= 0xC0) end--;
        try
        {
            StrictUtf8.GetString(sample[..end]);
            return true;
        }
        catch (DecoderFallbackException)
        {
        }
        int control = 0;
        foreach (byte b in sample)
            if (b < 0x20 && b is not (9 or 10 or 13 or 12)) control++;
        encoding = Encoding.Latin1;
        return control < sample.Length / 100 + 1;
    }

    private static string Decode(byte[] data, Encoding enc)
    {
        int skip = enc == Encoding.UTF8 && data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3
            : enc != Encoding.UTF8 && enc != Encoding.Latin1 ? 2 : 0;
        return enc.GetString(data, skip, data.Length - skip);
    }

    public static string HexDump(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 4 + 64);
        for (int off = 0; off < data.Length; off += 16)
        {
            sb.Append(off.ToString("X8")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (off + i < data.Length) sb.Append(data[off + i].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int i = 0; i < 16 && off + i < data.Length; i++)
            {
                byte b = data[off + i];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>File type recognition from the first bytes.</summary>
public static class Magic
{
    private static bool At(ReadOnlySpan<byte> d, int off, ReadOnlySpan<byte> sig) =>
        d.Length >= off + sig.Length && d.Slice(off, sig.Length).SequenceEqual(sig);

    public static bool IsImage(ReadOnlySpan<byte> d) =>
        Describe(d) is { } s && s.StartsWith("Immagine");

    public static string? Describe(ReadOnlySpan<byte> d)
    {
        if (At(d, 0, [0x89, (byte)'P', (byte)'N', (byte)'G'])) return "Immagine PNG";
        if (At(d, 0, [0xFF, 0xD8, 0xFF])) return "Immagine JPEG";
        if (At(d, 0, "GIF8"u8)) return "Immagine GIF";
        if (At(d, 0, "BM"u8) && d.Length > 14) return "Immagine BMP";
        if (At(d, 0, [(byte)'I', (byte)'I', 42, 0]) || At(d, 0, [(byte)'M', (byte)'M', 0, 42])) return "Immagine TIFF";
        if (At(d, 0, [0, 0, 1, 0]) && d.Length > 6 && d[4] > 0) return "Immagine ICO";
        if (At(d, 0, "RIFF"u8) && At(d, 8, "WEBP"u8)) return "Immagine WebP";
        if (At(d, 4, "ftypheic"u8) || At(d, 4, "ftypheix"u8) || At(d, 4, "ftypmif1"u8)) return "Immagine HEIC";
        if (At(d, 4, "ftypavif"u8)) return "Immagine AVIF";
        if (At(d, 0, [0x7F, (byte)'E', (byte)'L', (byte)'F'])) return "Eseguibile ELF";
        if (At(d, 0, [0x1F, 0x8B])) return "Archivio gzip";
        if (At(d, 0, [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0])) return "Archivio xz";
        if (At(d, 0, [0x28, 0xB5, 0x2F, 0xFD])) return "Archivio zstd";
        if (At(d, 0, "BZh"u8)) return "Archivio bzip2";
        if (At(d, 0, [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C])) return "Archivio 7z";
        if (At(d, 0, [(byte)'P', (byte)'K', 3, 4])) return "Archivio ZIP";
        if (At(d, 257, "ustar"u8)) return "Archivio tar";
        if (At(d, 0, "%PDF"u8)) return "Documento PDF";
        if (At(d, 0, "SQLite format 3\0"u8)) return "Database SQLite";
        if (At(d, 0, "MZ"u8)) return "Eseguibile Windows";
        if (At(d, 0, "OggS"u8)) return "Audio/video Ogg";
        if (At(d, 0, "fLaC"u8)) return "Audio FLAC";
        if (At(d, 0, "ID3"u8)) return "Audio MP3";
        if (At(d, 4, "ftyp"u8)) return "Video MP4/MOV";
        if (At(d, 0, [0x1A, 0x45, 0xDF, 0xA3])) return "Video Matroska/WebM";
        if (At(d, 0, "!<arch>\n"u8)) return "Archivio ar/deb";
        if (At(d, 1080, [0x53, 0xEF])) return "Filesystem ext (immagine disco)";
        return null;
    }
}
