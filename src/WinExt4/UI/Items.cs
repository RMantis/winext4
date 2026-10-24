using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WinExt4.Core;

namespace WinExt4.UI;

public sealed class SourceItem : INotifyPropertyChanged
{
    private string _stateText = "";
    private Ext4Source _source;

    public SourceItem(Ext4Source source) => _source = source;

    public Ext4Source Source
    {
        get => _source;
        set
        {
            if (_source == value) return;
            _source = value;
            OnChanged();
            OnChanged(nameof(Opacity));
        }
    }

    public double Opacity => Source.IsExt ? 1.0 : 0.55;

    /// <summary>"aperto", "scrittura", "montato su X:".</summary>
    public string StateText
    {
        get => _stateText;
        set { _stateText = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class EntryItem
{
    private static readonly Brush FolderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x9A, 0x1E));
    private static readonly Brush FileBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0));

    static EntryItem()
    {
        FolderBrush.Freeze();
        FileBrush.Freeze();
        LinkBrush.Freeze();
    }

    public required Ext4DirEntry Entry { get; init; }
    public required Ext4Stat Stat { get; init; }
    /// <summary>For symlinks: the target text and (if it resolves) the target's stat.</summary>
    public string? LinkTarget { get; init; }
    public Ext4Stat? TargetStat { get; init; }
    public uint TargetIno { get; init; }

    public string Name => Entry.NameIsExact ? Entry.Name : Entry.Name + " (nome non UTF-8)";
    public bool IsDirectory => Stat.IsDirectory;
    public bool IsLink => Stat.IsSymlink;
    public bool IsDirectoryLike => IsDirectory || TargetStat?.IsDirectory == true;

    public string Icon => IsDirectory ? "" : IsLink ? (TargetStat?.IsDirectory == true ? "" : "")
        : Stat.IsRegular ? "" : "";

    public Brush IconBrush => IsDirectory || TargetStat?.IsDirectory == true ? FolderBrush : IsLink ? LinkBrush : FileBrush;

    public string LinkText => IsLink ? $"→ {LinkTarget}" + (TargetStat is null ? " (non valido)" : "") : "";

    public string SizeText => IsDirectory || IsLink ? "" : Format.Size(Stat.Size);

    public string ModifiedText => Stat.ModifyTime.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    public string PermText => LinuxMode.ToString(Stat.Mode);

    public string OwnerText => $"{Stat.Uid}:{Stat.Gid}";

    public string TypeText => Stat.Type switch
    {
        Ext4FileType.Directory => "Cartella",
        Ext4FileType.Regular => FileKind(),
        Ext4FileType.Symlink => "Link simbolico",
        Ext4FileType.CharDevice => "Dispositivo (char)",
        Ext4FileType.BlockDevice => "Dispositivo (block)",
        Ext4FileType.Fifo => "FIFO",
        Ext4FileType.Socket => "Socket",
        _ => "Sconosciuto",
    };

    private string FileKind()
    {
        string ext = System.IO.Path.GetExtension(Entry.Name);
        return ext.Length > 1 ? $"File {ext.TrimStart('.').ToUpperInvariant()}" : "File";
    }
}
