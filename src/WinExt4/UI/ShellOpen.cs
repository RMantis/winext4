using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace WinExt4.UI;

/// <summary>A program registered to open a file type (what Explorer lists under "Open with").</summary>
public sealed class OpenWithHandler
{
    internal OpenWithHandler(ShellOpen.IAssocHandler handler, string name, ImageSource? icon)
    {
        Handler = handler;
        Name = name;
        Icon = icon;
    }

    internal ShellOpen.IAssocHandler Handler { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
}

/// <summary>Shell "Open with" helpers: handler enumeration, invocation and the system chooser dialog.</summary>
public static class ShellOpen
{
    [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAssocHandler
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
        [PreserveSig] int IsRecommended();
        [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int Invoke(ComIDataObject dataObject);
        [PreserveSig] int CreateInvoker(ComIDataObject dataObject, out IntPtr invoker);
    }

    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        // Always called with count = 1, so a single out parameter is enough.
        [PreserveSig]
        int Next(uint count, out IAssocHandler? handler, out uint fetched);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        // remaining methods unused
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        public string pcszFile;
        public string? pcszClass;
        public int oaifInFlags;
    }

    private const int ASSOC_FILTER_RECOMMENDED = 1;
    private const int OAIF_ALLOW_REGISTRATION = 0x1;
    private const int OAIF_EXEC = 0x4;
    private static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");
    private static readonly Guid IID_IDataObject = new("0000010e-0000-0000-C000-000000000046");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern IEnumAssocHandlers SHAssocEnumHandlers(string extension, int filter);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwnd, ref OPENASINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[] small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, System.Text.StringBuilder output, int size, IntPtr reserved);

    /// <summary>Recommended programs for an extension (".jpg"), best effort.</summary>
    public static List<OpenWithHandler> Handlers(string extension)
    {
        var list = new List<OpenWithHandler>();
        if (string.IsNullOrEmpty(extension)) return list;
        try
        {
            var e = SHAssocEnumHandlers(extension, ASSOC_FILTER_RECOMMENDED);
            if (e is null) return list;
            while (e.Next(1, out var h, out uint n) == 0 && n == 1 && h is not null)
            {
                if (h.GetUIName(out string name) != 0 || string.IsNullOrWhiteSpace(name)) continue;
                ImageSource? icon = null;
                if (h.GetIconLocation(out string path, out int index) == 0 && !string.IsNullOrEmpty(path))
                    icon = LoadIcon(path, index);
                if (list.All(x => x.Name != name))
                    list.Add(new OpenWithHandler(h, name, icon));
            }
        }
        catch (COMException)
        {
        }
        return list;
    }

    private static ImageSource? LoadIcon(string path, int index)
    {
        try
        {
            if (path.StartsWith('@'))
            {
                // Packaged apps: "@{Package?ms-resource://...}" resolves to an image file.
                var sb = new System.Text.StringBuilder(1024);
                if (SHLoadIndirectString(path, sb, sb.Capacity, IntPtr.Zero) != 0) return null;
                string file = sb.ToString();
                return File.Exists(file) ? new BitmapImage(new Uri(file)) { DecodePixelWidth = 16 } : null;
            }
            var small = new IntPtr[1];
            if (ExtractIconEx(Environment.ExpandEnvironmentVariables(path), index, null, small, 1) == 0 || small[0] == IntPtr.Zero)
                return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(small[0], System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DestroyIcon(small[0]);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Opens <paramref name="file"/> with the given handler.</summary>
    public static void Invoke(OpenWithHandler handler, string file)
    {
        SHCreateItemFromParsingName(file, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
        Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero, BHID_DataObject, IID_IDataObject, out object data));
        Marshal.ThrowExceptionForHR(handler.Handler.Invoke((ComIDataObject)data));
    }

    /// <summary>The system "How do you want to open this file?" dialog.</summary>
    public static void ShowChooser(IntPtr owner, string file)
    {
        var info = new OPENASINFO { pcszFile = file, oaifInFlags = OAIF_ALLOW_REGISTRATION | OAIF_EXEC };
        SHOpenWithDialog(owner, ref info);
    }
}
