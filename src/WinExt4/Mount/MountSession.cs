using System.IO;
using System.Runtime.InteropServices;
using Fsp;
using Microsoft.Win32;
using WinExt4.Core;

namespace WinExt4.Mount;

/// <summary>An ext volume mounted on a drive letter through WinFsp.</summary>
public sealed class MountSession : IDisposable
{
    private FileSystemHost? _host;

    public string DriveLetter { get; }
    public Ext4Volume Volume { get; }

    private MountSession(FileSystemHost host, string letter, Ext4Volume volume)
    {
        _host = host;
        DriveLetter = letter;
        Volume = volume;
    }

    /// <summary>True when the WinFsp runtime (and its signed driver) is installed.</summary>
    public static bool WinFspInstalled
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp")
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");
            return key?.GetValue("InstallDir") is string dir && File.Exists(Path.Combine(dir, "bin", "winfsp-x64.dll"));
        }
    }

    public static IEnumerable<string> FreeDriveLetters()
    {
        uint used = (uint)GetLogicalDrives();
        for (char c = 'Z'; c >= 'D'; c--)
            if ((used & (1u << (c - 'A'))) == 0)
                yield return $"{c}:";
    }

    public static MountSession Mount(Ext4Volume volume, AppSettings settings, string? letter)
    {
        if (!WinFspInstalled)
            throw new InvalidOperationException(
                "WinFsp non è installato. Scaricalo da https://winfsp.dev (driver firmato) e riprova.");

        if (string.IsNullOrEmpty(letter))
            letter = FreeDriveLetters().FirstOrDefault()
                     ?? throw new InvalidOperationException("Nessuna lettera di unità libera.");

        var fs = new Ext4FileSystem(volume, settings);
        var host = new FileSystemHost(fs);
        int status = host.Mount(letter, null, false, 0);
        if (status != FileSystemBase.STATUS_SUCCESS)
        {
            host.Dispose();
            throw new InvalidOperationException(
                $"WinFsp non è riuscito a montare l'unità {letter} (NTSTATUS 0x{status:X8}).");
        }
        return new MountSession(host, letter, volume);
    }

    public void Dispose()
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host is null) return;
        host.Unmount();
        host.Dispose();
    }

    [DllImport("kernel32.dll")]
    private static extern int GetLogicalDrives();
}
