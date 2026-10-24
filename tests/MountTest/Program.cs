// Mounts an ext image with WinFsp and drives it through ordinary .NET/Win32
// file APIs, the same way Explorer and other programs would.
//
//   dotnet run --project tests/MountTest -- <image> [ro]
//
// Exit code = number of failed checks. Run e2fsck on the image afterwards.
using System.Security.Cryptography;
using WinExt4.Core;
using WinExt4.Mount;

int fails = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
    if (!ok) fails++;
}
void Throws(Action a, string what)
{
    try { a(); Check(false, what + " (no error)"); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Check(true, what + $" -> {e.GetType().Name}"); }
}

bool isDevice = args[0].StartsWith(@"\\.\");
string image = isDevice ? args[0] : Path.GetFullPath(args[0]);
bool readOnly = args.Length > 1 && args[1] == "ro";
var src = isDevice
    ? new Ext4Source { Device = image, IsExt = true }
    : SourceScanner.ScanImage(image).First(s => s.IsExt);
var settings = new AppSettings { OwnerPolicy = OwnerPolicy.Fixed, FixedUid = 1234, FixedGid = 4321 };

using var vol = Ext4Volume.Open(src, !readOnly);
var mount = MountSession.Mount(vol, settings, null);
string root = mount.DriveLetter + "\\";
Console.WriteLine($"mounted {image} on {root} ({(readOnly ? "ro" : "rw")})");

try
{
    var drive = new DriveInfo(root);
    Check(drive.VolumeLabel.Length > 0, $"volume label '{drive.VolumeLabel}', fs {drive.DriveFormat}, {drive.TotalSize} bytes");
    var top = Directory.GetFileSystemEntries(root).Select(Path.GetFileName).ToList();
    Console.WriteLine("  root: " + string.Join(", ", top));
    Check(top.Contains("Case") && top.Contains("case"), "case-sensitive names both listed");
    Check(top.Contains("namewithcolons.txt"), "':' mapped to U+F03A");
    Check(File.ReadAllText(root + "hello.txt") == "hello from ext4\n", "read hello.txt");
    Check(File.ReadAllText(root + "dir\\città.txt") == "unicode: àèìòù €\n", "read UTF-8 name");
    Check(File.ReadAllText(root + "link-rel") == "hello from ext4\n", "relative symlink followed");
    Check(Directory.GetFiles(root + "dir").Length == 502, $"dir has {Directory.GetFiles(root + "dir").Length} files");
    Check(new FileInfo(root + "hello.txt").IsReadOnly == false, "u+w -> not READONLY");

    if (readOnly)
    {
        Throws(() => File.WriteAllText(root + "x.txt", "x"), "create on read-only mount");
        Throws(() => File.Delete(root + "hello.txt"), "delete on read-only mount");
    }
    else
    {
        // create / append / read back
        File.WriteAllText(root + "nuovo.txt", "prima riga\n");
        File.AppendAllText(root + "nuovo.txt", "seconda riga\n");
        Check(File.ReadAllText(root + "nuovo.txt") == "prima riga\nseconda riga\n", "create + append");
        var st = vol.Stat(vol.Lookup("/nuovo.txt"));
        Check(st.Uid == 1234 && st.Gid == 4321 && st.Permissions == Convert.ToUInt32("644", 8),
            $"owner/mode of new file {st.Uid}:{st.Gid} {Convert.ToString(st.Permissions, 8)}");

        // big file through the cache manager
        var data = RandomNumberGenerator.GetBytes(7 * 1024 * 1024 + 123);
        Directory.CreateDirectory(root + "cartella\\sotto");
        File.WriteAllBytes(root + "cartella\\sotto\\big.bin", data);
        var back = File.ReadAllBytes(root + "cartella\\sotto\\big.bin");
        Check(back.AsSpan().SequenceEqual(data), "7 MB write/read round trip");

        // overwrite with a shorter file (truncate)
        File.WriteAllText(root + "cartella\\sotto\\big.bin", "short");
        Check(new FileInfo(root + "cartella\\sotto\\big.bin").Length == 5, "overwrite truncates");

        // random access write in the middle
        using (var fs = new FileStream(root + "nuovo.txt", FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = 6;
            fs.Write("RIGA"u8);
        }
        Check(File.ReadAllText(root + "nuovo.txt") == "prima RIGA\nseconda riga\n", "write in the middle");

        // rename + move + case-only rename
        File.Move(root + "nuovo.txt", root + "cartella\\rinominato.txt");
        Check(File.Exists(root + "cartella\\rinominato.txt") && !File.Exists(root + "nuovo.txt"), "move file");
        File.Move(root + "cartella\\rinominato.txt", root + "cartella\\Rinominato.txt");
        Check(Directory.GetFiles(root + "cartella").Select(Path.GetFileName).Contains("Rinominato.txt"), "case-only rename");
        Directory.Move(root + "cartella\\sotto", root + "spostata");
        Check(Directory.Exists(root + "spostata") && File.Exists(root + "spostata\\big.bin"), "move directory");
        File.Copy(root + "hello.txt", root + "spostata\\hello-copia.txt");
        Check(File.ReadAllText(root + "spostata\\hello-copia.txt") == "hello from ext4\n", "copy inside the drive");
        File.Copy(root + "hello.txt", root + "spostata\\hello-copia.txt", overwrite: true);
        Check(File.Exists(root + "spostata\\hello-copia.txt"), "copy with overwrite");

        // attributes and times
        var t = new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(root + "spostata\\hello-copia.txt", t);
        Check(File.GetLastWriteTimeUtc(root + "spostata\\hello-copia.txt") == t, "set mtime");
        File.SetAttributes(root + "spostata\\hello-copia.txt", FileAttributes.ReadOnly);
        st = vol.Stat(vol.Lookup("/spostata/hello-copia.txt"));
        Check((st.Permissions & 0x92) == 0, $"READONLY clears w bits ({Convert.ToString(st.Permissions, 8)})");
        Throws(() => File.WriteAllText(root + "spostata\\hello-copia.txt", "x"), "write to read-only file");
        File.SetAttributes(root + "spostata\\hello-copia.txt", FileAttributes.Normal);
        st = vol.Stat(vol.Lookup("/spostata/hello-copia.txt"));
        Check((st.Permissions & 0x80) != 0, "clearing READONLY restores u+w");

        // deletes
        Throws(() => Directory.Delete(root + "spostata"), "delete non-empty directory");
        File.Delete(root + "spostata\\hello-copia.txt");
        Check(!File.Exists(root + "spostata\\hello-copia.txt"), "delete file");
        Directory.Delete(root + "cartella", recursive: true);
        Check(!Directory.Exists(root + "cartella"), "recursive directory delete");
        foreach (var f in Directory.GetFiles(root + "dir", "many_1*")) File.Delete(f);
        Check(Directory.GetFiles(root + "dir", "many_1*").Length == 0, "bulk delete");

        // name mapping on create
        File.WriteAllText(root + "ab.txt", "colon");
        Check(vol.TryLookup("/a:b.txt", out _), "U+F03A creates a real ':' on ext4");
        File.WriteAllText(root + "spazio finale", "x");
        Check(vol.TryLookup("/spazio finale ", out _), "trailing space mapped");

        var free1 = new DriveInfo(root).AvailableFreeSpace;
        Check(free1 > 0, $"free space {free1}");
    }
}
catch (Exception e)
{
    Console.WriteLine("  EXCEPTION " + e);
    fails++;
}
finally
{
    mount.Dispose();
}
vol.Dispose();
Console.WriteLine(fails == 0 ? "ALL MOUNT TESTS PASSED" : $"{fails} FAILURES");
return fails;
