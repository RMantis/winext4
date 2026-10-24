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

    public static HashResult HashFile(string path, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        using var dev = RawDevice.Open(path, 0, 0, false);
        return HashDevice(dev, dev.Size, "Verifica dell'immagine", progress, cancel);
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    /// <summary>Copies a range to an image file, hashing on the way; optional sparse output and read-back check.</summary>
    public static HashResult Export(RawTarget t, string imagePath, bool sparse, bool verify,
        IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        HashResult result;
        using (var dev = RawDevice.Open(t.Path, t.Offset, t.Length, false))
        using (var fs = new FileStream(imagePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            if (sparse) MakeSparse(fs);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var state = new JobProgress { Phase = "Esportazione", Total = (long)dev.Size };
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
                    if (sparse && IsZero(buf.AsSpan(0, n)))
                        fs.Seek(n, SeekOrigin.Current); // leave a hole
                    else
                        fs.Write(buf, 0, n);
                    pos += (ulong)n;
                    Report(progress, state, (long)pos);
                }
                fs.SetLength((long)dev.Size);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
            result = new HashResult(Hex(md5.GetHashAndReset()), Hex(sha.GetHashAndReset()), dev.Size);
        }
        if (verify)
        {
            var check = HashFile(imagePath, progress, cancel);
            if (check.Sha256 != result.Sha256)
                throw new IOException("L'immagine scritta non corrisponde ai dati letti (hash diverso).");
        }
        Sidecar.Write(imagePath, result);
        return result;
    }

    public sealed record WriteOutcome(HashResult Image, HashResult? Readback, string? ExpectedFromSidecar);

    /// <summary>
    /// Writes an image over a whole disk or a partition. The image hash is
    /// computed while writing and compared with <paramref name="expected"/>
    /// (from a .md5/.sha256 file) and, if requested, with a read-back of the target.
    /// </summary>
    public static WriteOutcome WriteImage(string imagePath, RawTarget target, Sidecar.Entry? expected, bool preVerify,
        bool readback, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        ulong imageSize = (ulong)new FileInfo(imagePath).Length;
        if (target.Length > 0 && imageSize > target.Length)
            throw new IOException($"L'immagine ({Format.Size(imageSize)}) è più grande della destinazione ({Format.Size(target.Length)}).");

        if (expected is not null && preVerify)
        {
            var pre = HashFile(imagePath, progress, cancel);
            if (!expected.Matches(pre))
                throw new IOException($"L'immagine non corrisponde al file {expected.Algorithm.ToUpperInvariant()} ({expected.File}): non è stato scritto niente.");
        }

        HashResult written;
        using (var dev = RawDevice.Open(target.Path, target.Offset, target.Length, true))
        using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            if (imageSize > dev.Size)
                throw new IOException($"L'immagine ({Format.Size(imageSize)}) è più grande della destinazione ({Format.Size(dev.Size)}).");
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var state = new JobProgress { Phase = "Scrittura", Total = (long)imageSize };
            byte[] buf = ArrayPool<byte>.Shared.Rent(Chunk);
            try
            {
                for (ulong pos = 0; pos < imageSize;)
                {
                    cancel.ThrowIfCancellationRequested();
                    int n = fs.Read(buf, 0, (int)Math.Min((ulong)Chunk, imageSize - pos));
                    if (n <= 0) throw new IOException("Lettura dell'immagine interrotta.");
                    md5.AppendData(buf, 0, n);
                    sha.AppendData(buf, 0, n);
                    // keep writes sector aligned: only the last chunk may be short
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
            if (expected is not null && !expected.Matches(written))
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
    public sealed record Entry(string Algorithm, string Hash, string File)
    {
        public bool Matches(HashResult r) => Algorithm == "md5"
            ? string.Equals(Hash, r.Md5, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Hash, r.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static void Write(string imagePath, HashResult r)
    {
        string name = Path.GetFileName(imagePath);
        File.WriteAllText(imagePath + ".md5", $"{r.Md5} *{name}\n");
        File.WriteAllText(imagePath + ".sha256", $"{r.Sha256} *{name}\n");
    }

    /// <summary>Finds image.img.sha256 / image.sha256 / .md5 next to the image (SHA-256 preferred).</summary>
    public static Entry? Find(string imagePath)
    {
        string name = Path.GetFileName(imagePath);
        string noExt = Path.ChangeExtension(imagePath, null);
        foreach (var (algo, ext, len) in new[] { ("sha256", ".sha256", 64), ("md5", ".md5", 32) })
            foreach (var candidate in new[] { imagePath + ext, noExt + ext, imagePath + ext + "sum", Path.Combine(Path.GetDirectoryName(imagePath)!, algo.ToUpperInvariant() + "SUMS") })
            {
                if (!File.Exists(candidate)) continue;
                foreach (var line in File.ReadLines(candidate))
                {
                    var (hash, file) = ParseLine(line);
                    if (hash is null || hash.Length != len) continue;
                    if (file is null || string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                        return new Entry(algo, hash, Path.GetFileName(candidate));
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
