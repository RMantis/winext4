using System.IO;
using System.Text.Json;

namespace WinExt4.Core;

/// <summary>How new files get their Linux owner.</summary>
public enum OwnerPolicy
{
    /// <summary>Same uid/gid as the directory they are created in.</summary>
    InheritParent,
    /// <summary>Always <see cref="AppSettings.FixedUid"/>/<see cref="AppSettings.FixedGid"/>.</summary>
    Fixed,
}

public sealed class AppSettings
{
    public OwnerPolicy OwnerPolicy { get; set; } = OwnerPolicy.InheritParent;
    public uint FixedUid { get; set; } = 1000;
    public uint FixedGid { get; set; } = 1000;
    public uint FileMode { get; set; } = Convert.ToUInt32("644", 8);
    public uint DirMode { get; set; } = Convert.ToUInt32("755", 8);
    public bool HideDotFiles { get; set; }
    public bool AutoOpenOnInsert { get; set; } = true;
    public bool ShowAllPartitions { get; set; }
    public bool OpenExplorerAfterMount { get; set; } = true;
    public string PreferredDriveLetter { get; set; } = "";
    public List<string> RecentImages { get; set; } = new();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinExt4", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A corrupt settings file should never keep the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
        }
    }

    public (uint Uid, uint Gid) OwnerFor(Ext4Stat parentDir) =>
        OwnerPolicy == OwnerPolicy.InheritParent ? (parentDir.Uid, parentDir.Gid) : (FixedUid, FixedGid);

    public void AddRecentImage(string path)
    {
        RecentImages.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentImages.Insert(0, path);
        if (RecentImages.Count > 10) RecentImages.RemoveRange(10, RecentImages.Count - 10);
    }
}
