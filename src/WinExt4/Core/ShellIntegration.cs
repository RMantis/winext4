using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinExt4.Core;

/// <summary>
/// Per-user registration in Explorer's "Open with" list and an "Apri con
/// WinExt4" context-menu verb for disk images. Never changes the default
/// program of any file type.
/// </summary>
public static class ShellIntegration
{
    public static readonly string[] Extensions =
    {
        ".img", ".raw", ".bin", ".dd", ".ext2", ".ext3", ".ext4", ".gz", ".tgz", ".xz", ".zst", ".zip", ".tar",
    };

    private const string ProgId = "WinExt4.DiskImage";
    private const string AppKey = @"Software\Classes\Applications\WinExt4.exe";
    private const string ProgIdKey = @"Software\Classes\" + ProgId;
    private const string VerbName = "WinExt4.Open";

    public static bool IsRegistered
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(AppKey + @"\shell\open\command");
            return k?.GetValue(null) is string cmd && cmd.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>WinExt4.exe, also when this code runs inside another host (tests).</summary>
    internal static string Exe
    {
        get
        {
            string loc = typeof(ShellIntegration).Assembly.Location;
            if (loc.Length > 0)
            {
                string exe = System.IO.Path.ChangeExtension(loc, ".exe");
                if (System.IO.File.Exists(exe)) return exe;
            }
            return Environment.ProcessPath ?? throw new InvalidOperationException("Percorso dell'eseguibile sconosciuto.");
        }
    }

    public static void Register()
    {
        string command = $"\"{Exe}\" \"%1\"";
        string icon = $"\"{Exe}\",0";

        using (var app = Registry.CurrentUser.CreateSubKey(AppKey))
        {
            app.SetValue("FriendlyAppName", "WinExt4");
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, command);
            using (var types = app.CreateSubKey("SupportedTypes"))
                foreach (var ext in Extensions) types.SetValue(ext, "");
            using (var di = app.CreateSubKey("DefaultIcon")) di.SetValue(null, icon);
        }
        using (var prog = Registry.CurrentUser.CreateSubKey(ProgIdKey))
        {
            prog.SetValue(null, "Immagine disco (WinExt4)");
            using (var di = prog.CreateSubKey("DefaultIcon")) di.SetValue(null, icon);
            using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, command);
        }
        foreach (var ext in Extensions)
        {
            using (var owp = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids"))
                owp.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            // Context-menu verb shown whatever the default program is.
            using var verb = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{ext}\shell\{VerbName}");
            verb.SetValue("MUIVerb", "Apri con WinExt4");
            verb.SetValue("Icon", icon);
            using var vcmd = verb.CreateSubKey("command");
            vcmd.SetValue(null, command);
        }
        NotifyShell();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(AppKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(ProgIdKey, false);
        foreach (var ext in Extensions)
        {
            using (var owp = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", true))
                owp?.DeleteValue(ProgId, false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{ext}\shell\{VerbName}", false);
            // Remove the keys we may have created, but only when nothing else lives there.
            DeleteIfEmpty($@"Software\Classes\{ext}\OpenWithProgids");
            DeleteIfEmpty($@"Software\Classes\{ext}");
            DeleteIfEmpty($@"Software\Classes\SystemFileAssociations\{ext}\shell");
            DeleteIfEmpty($@"Software\Classes\SystemFileAssociations\{ext}");
        }
        NotifyShell();
    }

    private static void DeleteIfEmpty(string path)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(path))
        {
            if (k is null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
        }
        Registry.CurrentUser.DeleteSubKey(path, false);
    }

    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
