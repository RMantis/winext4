using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WinExt4.Native;

namespace WinExt4.Core;

/// <summary>Byte-level access to a disk, a partition or an image file.</summary>
public sealed unsafe class RawDevice : IDisposable
{
    private IntPtr _h;

    public string Path { get; }
    public ulong Size { get; }

    private RawDevice(IntPtr h, string path)
    {
        _h = h;
        Path = path;
        Ext4Native.e4_raw_size(h, out ulong size);
        Size = size;
    }

    /// <param name="write">Locks the volume (for \\.\PhysicalDriveN: every volume on the disk).</param>
    public static RawDevice Open(string path, ulong offset, ulong length, bool write)
    {
        Ext4Exception.Check(Ext4Native.e4_raw_open(path, offset, length, write ? Ext4Native.RawWrite : 0, out IntPtr h),
            write ? "Apertura in scrittura" : "Apertura");
        return new RawDevice(h, path);
    }

    public void Read(ulong pos, Span<byte> buf)
    {
        fixed (byte* p = buf)
            Ext4Exception.Check(Ext4Native.e4_raw_read(_h, pos, p, (uint)buf.Length), "Lettura");
    }

    public void Write(ulong pos, ReadOnlySpan<byte> buf)
    {
        fixed (byte* p = buf)
            Ext4Exception.Check(Ext4Native.e4_raw_write(_h, pos, p, (uint)buf.Length), "Scrittura");
    }

    public void Flush() => Ext4Exception.Check(Ext4Native.e4_raw_flush(_h), "Scrittura su disco");

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _h, IntPtr.Zero);
        if (h != IntPtr.Zero) Ext4Native.e4_raw_close(h);
    }

    /// <summary>Disk numbers a volume (e.g. "C:") lives on.</summary>
    public static int[] DisksOfVolume(string driveLetter)
    {
        var disks = stackalloc int[16];
        int n = Ext4Native.e4_volume_disks($@"\\.\{driveLetter.TrimEnd('\\')}", disks, 16);
        return n <= 0 ? Array.Empty<int>() : new ReadOnlySpan<int>(disks, n).ToArray();
    }
}

public sealed class JobProgress
{
    public string Phase { get; set; } = "";
    public long Done { get; set; }
    public long Total { get; set; }
}

public sealed record HashResult(string Md5, string Sha256, ulong Bytes);

/// <summary>A byte range to hash, export or overwrite: a partition, a whole disk or an image.</summary>
public sealed record RawTarget(string Path, ulong Offset, ulong Length, string Description, int DiskNumber = -1)
{
    public static RawTarget Partition(Ext4Source s) =>
        new(s.Device, s.Offset, s.Length,
            s.IsImage ? $"{System.IO.Path.GetFileName(s.ImagePath)}, partizione {s.PartitionNumber}" : $"Disco {s.DiskNumber}, partizione {s.PartitionNumber}",
            s.IsImage ? -1 : s.DiskNumber);

    public static RawTarget WholeDisk(Ext4Source s) =>
        new(s.Container, 0, s.IsImage ? 0 : s.DiskSize,
            s.IsImage ? System.IO.Path.GetFileName(s.ImagePath)! : $"Disco {s.DiskNumber} ({s.Model})",
            s.IsImage ? -1 : s.DiskNumber);
}

/// <summary>Hashing, disk/partition → image export and image → disk writing.</summary>
public static class Imaging
{
    private const int Chunk = 4 << 20;

    private static void Report(IProgress<JobProgress>? p, JobProgress state, long done)
    {
        state.Done = done;
        p?.Report(state);
    }

    /// <summary>MD5 and SHA-256 of a range, in one pass.</summary>
    public static HashResult Hash(RawTarget t, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        using var dev = RawDevice.Open(t.Path, t.Offset, t.Length, false);
        return HashDevice(dev, dev.Size, "Calcolo hash", progress, cancel);
    }

    private static HashResult HashDevice(RawDevice dev, ulong size, string phase, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var state = new JobProgress { Phase = phase, Total = (long)size };
        byte[] buf = ArrayPool<byte>.Shared.Rent(Chunk);
        try
        {
            for (ulong pos = 0; pos < size;)
            {
                cancel.ThrowIfCancellationRequested();
                int n = (int)Math.Min((ulong)Chunk, size - pos);
                dev.Read(pos, buf.AsSpan(0, n));
                md5.AppendData(buf, 0, n);
                sha.AppendData(buf, 0, n);
                pos += (ulong)n;
                Report(progress, state, (long)pos);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return new HashResult(Hex(md5.GetHashAndReset()), Hex(sha.GetHashAndReset()), size);
    }

    public static HashResult HashFile(string path, IProgress<JobProgress>? progress, CancellationToken cancel) =>
        HashImageContent(path, "Verifica dell'immagine", progress, cancel);

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    public sealed record ExportResult(HashResult Raw, string? FileMd5, string? FileSha256, long FileBytes);

    /// <summary>
    /// Copies a range to an image file in the given format, hashing the raw
    /// data on the way (and the output file for compressed/VHD formats).
    /// Sidecars: image.img.{md5,sha256} for raw content; for compressed
    /// output also &lt;output&gt;.{md5,sha256} for the file itself.
    /// </summary>
    public static ExportResult Export(RawTarget t, string outPath, ImageFormat format, bool sparse, bool verify,
        IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        HashResult raw;
        string? fileMd5 = null, fileSha = null;
        using (var dev = RawDevice.Open(t.Path, t.Offset, t.Length, false))
        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
        {
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var state = new JobProgress { Phase = "Esportazione", Total = (long)dev.Size };
            HashingStream? hashing = null;
            Stream? comp = null;
            DynamicVhdWriter? vhd = null;
            switch (format)
            {
                case ImageFormat.Raw:
                    if (sparse) MakeSparse(fs);
                    break;
                case ImageFormat.Vhd:
                    MakeSparse(fs);
                    vhd = new DynamicVhdWriter(fs, dev.Size);
                    break;
                default:
                    hashing = new HashingStream(fs);
                    comp = ImageFormats.Compressor(hashing, format);
                    break;
            }
            byte[] buf = ArrayPool<byte>.Shared.Rent(Chunk);
            try
            {
                for (ulong pos = 0; pos < dev.Size;)
                {
                    cancel.ThrowIfCancellationRequested();
                    int n = (int)Math.Min((ulong)Chunk, dev.Size - pos);
                    dev.Read(pos, buf.AsSpan(0, n));
                    md5.AppendData(buf, 0, n);
                    sha.AppendData(buf, 0, n);
                    if (comp is not null) comp.Write(buf, 0, n);
                    else if (vhd is not null) vhd.Write(buf.AsSpan(0, n));
                    else if (sparse && IsZero(buf.AsSpan(0, n))) fs.Seek(n, SeekOrigin.Current); // leave a hole
                    else fs.Write(buf, 0, n);
                    pos += (ulong)n;
                    Report(progress, state, (long)pos);
                }
                if (format == ImageFormat.Raw) fs.SetLength((long)dev.Size);
                comp?.Dispose();
                vhd?.Finish();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
            raw = new HashResult(Hex(md5.GetHashAndReset()), Hex(sha.GetHashAndReset()), dev.Size);
            if (hashing is not null) (fileMd5, fileSha) = hashing.Finish();
        }
        if (format == ImageFormat.Vhd)
        {
            var f = HashPlainFile(outPath, progress, cancel);
            (fileMd5, fileSha) = (f.Md5, f.Sha256);
        }
        if (verify)
        {
            var check = HashImageContent(outPath, "Verifica dell'immagine", progress, cancel);
            if (check.Sha256 != raw.Sha256)
                throw new IOException("L'immagine scritta non corrisponde ai dati letti (hash diverso).");
        }
        if (format == ImageFormat.Raw)
        {
            Sidecar.Write(outPath, raw);
        }
        else
        {
            Sidecar.Write(outPath, new HashResult(fileMd5!, fileSha!, (ulong)new FileInfo(outPath).Length));
            Sidecar.Write(Sidecar.RawName(outPath), raw);
        }
        return new ExportResult(raw, fileMd5, fileSha, new FileInfo(outPath).Length);
    }

    /// <summary>Hash of a file's bytes as stored (no decompression).</summary>
    public static HashResult HashPlainFile(string path, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return HashStream(fs, fs.Length, "Calcolo hash del file", progress, cancel);
    }

    /// <summary>Hash of the raw disk content of an image (decompressing gz/zst/xz/vhd).</summary>
    public static HashResult HashImageContent(string path, string phase, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        using var s = ImageFormats.OpenRead(path, out _, out long? len);
        return HashStream(s, len ?? new FileInfo(path).Length, phase, progress, cancel);
    }

    private static HashResult HashStream(Stream s, long expected, string phase, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var state = new JobProgress { Phase = phase, Total = expected };
        byte[] buf = ArrayPool<byte>.Shared.Rent(Chunk);
        long total = 0;
        try
        {
            int n;
            while ((n = s.Read(buf, 0, Chunk)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                md5.AppendData(buf, 0, n);
                sha.AppendData(buf, 0, n);
                total += n;
                if (total > state.Total) state.Total = total;
                Report(progress, state, total);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return new HashResult(Hex(md5.GetHashAndReset()), Hex(sha.GetHashAndReset()), (ulong)total);
    }

    public sealed record WriteOutcome(HashResult Image, HashResult? Readback, string? ExpectedFromSidecar);

    /// <summary>
    /// Writes an image (raw, gz, zst, xz or VHD) over a whole disk or a
    /// partition. Compressed images get a verification pass first, which also
    /// gives their size, so nothing is written when the image is too big or
    /// does not match its .md5/.sha256. After writing the target is read back.
    /// </summary>
    public static WriteOutcome WriteImage(string imagePath, RawTarget target, Sidecar.Entry? expected, bool preVerify,
        bool readback, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        var format = ImageFormats.Detect(imagePath);
        ulong imageSize;
        if (format == ImageFormat.Raw)
        {
            imageSize = (ulong)new FileInfo(imagePath).Length;
            if (expected is not null && preVerify && !expected.Matches(HashPlainFile(imagePath, progress, cancel), null))
                throw new IOException($"L'immagine non corrisponde al file {expected.Algorithm.ToUpperInvariant()} ({expected.File}): non è stato scritto niente.");
        }
        else
        {
            var content = HashImageContent(imagePath, "Verifica dell'immagine compressa", progress, cancel);
            imageSize = content.Bytes;
            if (expected is not null)
            {
                var file = expected.AppliesToRaw ? null : HashPlainFile(imagePath, progress, cancel);
                if (!expected.Matches(file, content))
                    throw new IOException($"L'immagine non corrisponde al file {expected.Algorithm.ToUpperInvariant()} ({expected.File}): non è stato scritto niente.");
            }
        }
        if (target.Length > 0 && imageSize > target.Length)
            throw new IOException($"L'immagine ({Format.Size(imageSize)}) è più grande della destinazione ({Format.Size(target.Length)}).");

        HashResult written;
        using (var dev = RawDevice.Open(target.Path, target.Offset, target.Length, true))
        using (var input = ImageFormats.OpenRead(imagePath, out _, out _))
        {
            if (imageSize > dev.Size)
                throw new IOException($"L'immagine ({Format.Size(imageSize)}) è più grande della destinazione ({Format.Size(dev.Size)}).");
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var state = new JobProgress { Phase = "Scrittura", Total = (long)imageSize };
            byte[] buf = ArrayPool<byte>.Shared.Rent(Chunk);
            try
            {
                ulong pos = 0;
                while (pos < imageSize)
                {
                    cancel.ThrowIfCancellationRequested();
                    // fill whole chunks so writes stay large and sector aligned
                    int n = 0, want = (int)Math.Min((ulong)Chunk, imageSize - pos), r;
                    while (n < want && (r = input.Read(buf, n, want - n)) > 0) n += r;
                    if (n == 0) throw new IOException("Lettura dell'immagine interrotta.");
                    md5.AppendData(buf, 0, n);
                    sha.AppendData(buf, 0, n);
                    dev.Write(pos, buf.AsSpan(0, n));
                    pos += (ulong)n;
                    Report(progress, state, (long)pos);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
            dev.Flush();
            written = new HashResult(Hex(md5.GetHashAndReset()), Hex(sha.GetHashAndReset()), imageSize);

            HashResult? back = null;
            if (readback)
            {
                back = HashDevice(dev, imageSize, "Verifica (rilettura)", progress, cancel);
                if (back.Sha256 != written.Sha256)
                    throw new IOException("La rilettura del disco non corrisponde all'immagine: la scrittura potrebbe essere fallita o il dispositivo è difettoso.");
            }
            if (format == ImageFormat.Raw && expected is not null && !expected.Matches(written, null))
                throw new IOException($"L'immagine scritta non corrisponde al file {expected.Algorithm.ToUpperInvariant()} ({expected.File}).");
            return new WriteOutcome(written, back, expected?.File);
        }
    }

    private static bool IsZero(ReadOnlySpan<byte> s) => s.IndexOfAnyExcept((byte)0) < 0;

    private static void MakeSparse(FileStream fs)
    {
        const uint FSCTL_SET_SPARSE = 0x000900C4;
        DeviceIoControl(fs.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, IntPtr inBuf, int inSize,
        IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    /// <summary>Disks Windows runs from: never offered as write targets.</summary>
    public static HashSet<int> SystemDisks()
    {
        var set = new HashSet<int>();
        foreach (var letter in new[] { Environment.GetEnvironmentVariable("SystemDrive"), System.IO.Path.GetPathRoot(Environment.SystemDirectory) })
            if (!string.IsNullOrEmpty(letter))
                foreach (int d in RawDevice.DisksOfVolume(letter[..2]))
                    set.Add(d);
        return set;
    }

    /// <summary>Drive letters that live on a disk (they will disappear while it is written).</summary>
    public static List<string> LettersOnDisk(int disk) =>
        DriveInfo.GetDrives().Select(d => d.Name[..2])
            .Where(l => RawDevice.DisksOfVolume(l).Contains(disk)).ToList();
}

/// <summary>md5sum/sha256sum style sidecar files ("hash *name").</summary>
public static class Sidecar
{
    /// <param name="AppliesToRaw">
    /// The hash is of the decompressed content (a "disk.img.sha256" next to
    /// "disk.img.gz"), not of the file itself.
    /// </param>
    public sealed record Entry(string Algorithm, string Hash, string File, bool AppliesToRaw = false)
    {
        private bool Eq(HashResult? r) => r is not null && (Algorithm == "md5"
            ? string.Equals(Hash, r.Md5, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Hash, r.Sha256, StringComparison.OrdinalIgnoreCase));

        /// <param name="file">Hash of the file as stored.</param>
        /// <param name="content">Hash of the decompressed content (null for raw images: same as file).</param>
        public bool Matches(HashResult? file, HashResult? content) =>
            AppliesToRaw ? Eq(content ?? file) : Eq(file);
    }

    public static readonly string[] CompressedExtensions = { ".gz", ".zst", ".xz", ".vhd" };

    /// <summary>"disk.img.gz" → "disk.img"; "disk.vhd" → "disk.img".</summary>
    public static string RawName(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".vhd") return Path.ChangeExtension(path, ".img");
        if (CompressedExtensions.Contains(ext))
        {
            string inner = path[..^ext.Length];
            return Path.GetExtension(inner).Length > 0 ? inner : inner + ".img";
        }
        return path;
    }

    public static void Write(string imagePath, HashResult r)
    {
        string name = Path.GetFileName(imagePath);
        File.WriteAllText(imagePath + ".md5", $"{r.Md5} *{name}\n");
        File.WriteAllText(imagePath + ".sha256", $"{r.Sha256} *{name}\n");
    }

    /// <summary>
    /// Finds a hash for the image: image.sha256 / image.md5 / SHA256SUMS…, also
    /// one written for the decompressed content (disk.img.sha256 next to disk.img.gz).
    /// SHA-256 is preferred.
    /// </summary>
    public static Entry? Find(string imagePath)
    {
        string name = Path.GetFileName(imagePath);
        string rawPath = RawName(imagePath);
        string rawName = Path.GetFileName(rawPath);
        string dir = Path.GetDirectoryName(imagePath)!;
        foreach (var (algo, ext, len) in new[] { ("sha256", ".sha256", 64), ("md5", ".md5", 32) })
        {
            var candidates = new[]
            {
                imagePath + ext, Path.ChangeExtension(imagePath, null) + ext, imagePath + ext + "sum",
                rawPath + ext, Path.Combine(dir, algo.ToUpperInvariant() + "SUMS"),
            }.Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                foreach (var line in File.ReadLines(candidate))
                {
                    var (hash, file) = ParseLine(line);
                    if (hash is null || hash.Length != len) continue;
                    string? f = file is null ? null : Path.GetFileName(file);
                    if (f is null && candidate.Equals(rawPath + ext, StringComparison.OrdinalIgnoreCase) && rawPath != imagePath)
                        return new Entry(algo, hash, Path.GetFileName(candidate), AppliesToRaw: true);
                    if (f is null || string.Equals(f, name, StringComparison.OrdinalIgnoreCase))
                        return new Entry(algo, hash, Path.GetFileName(candidate));
                    if (rawPath != imagePath && string.Equals(f, rawName, StringComparison.OrdinalIgnoreCase))
                        return new Entry(algo, hash, Path.GetFileName(candidate), AppliesToRaw: true);
                }
            }
        }
        return null;
    }

    /// <summary>"hash  name", "hash *name" or a bare hash.</summary>
    public static (string? Hash, string? File) ParseLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith('#')) return (null, null);
        int sp = line.IndexOfAny(new[] { ' ', '\t' });
        if (sp < 0) return (line.All(Uri.IsHexDigit) ? line : null, null);
        string hash = line[..sp];
        string file = line[(sp + 1)..].TrimStart(' ', '\t');
        if (file.StartsWith('*')) file = file[1..];
        return (hash.All(Uri.IsHexDigit) ? hash : null, file);
    }
}
