using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinExt4.Core;

namespace WinExt4.UI;

/// <summary>Base for the small code-only dialogs: consistent look, Esc/Enter handling.</summary>
public abstract class SimpleDialog : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(18, 16, 18, 8) };
    protected readonly StackPanel Buttons = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(18, 4, 18, 16),
    };

    protected SimpleDialog(string title, double width = 420)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        var root = new DockPanel();
        DockPanel.SetDock(Buttons, Dock.Bottom);
        root.Children.Add(Buttons);
        root.Children.Add(Body);
        Content = root;
    }

    protected Button AddButton(string text, bool primary, RoutedEventHandler click)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = primary,
        };
        if (primary) b.Style = (Style)Application.Current.FindResource("PrimaryButton");
        else b.Padding = new Thickness(14, 5, 14, 5);
        b.Click += click;
        Buttons.Children.Add(b);
        return b;
    }
}

public sealed class InputDialog : SimpleDialog
{
    private readonly TextBox _box;

    public string Value => _box.Text.Trim();

    public InputDialog(string title, string prompt, string initial) : base(title)
    {
        Body.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });
        _box = new TextBox { Text = initial, Padding = new Thickness(4, 3, 4, 3) };
        Body.Children.Add(_box);
        Body.Children.Add(new TextBlock
        {
            Text = "I nomi ext sono sensibili a maiuscole/minuscole e possono contenere qualsiasi carattere tranne «/».",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MutedBrush"),
            FontSize = 11.5,
            Margin = new Thickness(0, 6, 0, 0),
        });
        AddButton("OK", true, (_, _) =>
        {
            if (Value.Length == 0 || Value.Contains('/') || Value is "." or "..") return;
            DialogResult = true;
        });
        AddButton("Annulla", false, (_, _) => DialogResult = false).IsCancel = true;
        Loaded += (_, _) =>
        {
            _box.Focus();
            int dot = initial.LastIndexOf('.');
            if (dot > 0) _box.Select(0, dot); else _box.SelectAll();
        };
    }
}

public sealed class ConflictDialog : SimpleDialog
{
    public ConflictChoice Choice { get; private set; } = ConflictChoice.Cancel;
    public bool ApplyToAll { get; private set; }

    public ConflictDialog(string name) : base("Elemento già esistente", 460)
    {
        Body.Children.Add(new TextBlock
        {
            Text = $"«{System.IO.Path.GetFileName(name)}» esiste già nella destinazione.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
        });
        Body.Children.Add(new TextBlock
        {
            Text = "Vuoi sostituirlo?",
            Margin = new Thickness(0, 6, 0, 0),
        });
        AddButton("Sostituisci", true, (_, _) => Close(ConflictChoice.Overwrite, false));
        AddButton("Sostituisci tutti", false, (_, _) => Close(ConflictChoice.Overwrite, true));
        AddButton("Salta", false, (_, _) => Close(ConflictChoice.Skip, false));
        AddButton("Annulla", false, (_, _) => Close(ConflictChoice.Cancel, false)).IsCancel = true;
    }

    private void Close(ConflictChoice c, bool all)
    {
        Choice = c;
        ApplyToAll = all;
        DialogResult = true;
    }
}

public sealed class VolumeInfoDialog : SimpleDialog
{
    public VolumeInfoDialog(Ext4Volume vol, Ext4Source? source) : base("Informazioni sul filesystem", 480)
    {
        var i = vol.GetInfo();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string k, string v)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var a = new TextBlock { Text = k, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MutedBrush"), Margin = new Thickness(0, 2, 0, 2) };
            var b = new TextBox { Text = v, IsReadOnly = true, BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(a, r); Grid.SetRow(b, r); Grid.SetColumn(b, 1);
            grid.Children.Add(a); grid.Children.Add(b);
        }
        Row("Tipo", i.Kind);
        Row("Etichetta", i.Label.Length > 0 ? i.Label : "(nessuna)");
        Row("UUID", i.Uuid);
        Row("Dimensione", Format.Size(i.TotalBytes));
        Row("Spazio disponibile", Format.Size(i.FreeBytes));
        Row("Riservato (root)", Format.Size(i.ReservedBytes));
        Row("Dimensione blocco", $"{i.BlockSize} byte");
        Row("Inode liberi", $"{i.FreeInodes:N0} di {i.InodesCount:N0}");
        Row("Journal", i.HasJournal ? (i.NeedsRecovery ? "presente, da riprodurre" : i.JournalReplayed ? "presente, riprodotto all'apertura" : "presente") : "assente");
        Row("Stato", i.HasErrors ? "con errori" : i.Clean ? "pulito" : "non smontato correttamente");
        Row("Ultimo montaggio", i.LastMount?.ToString("g") ?? "-");
        if (i.LastMountedOn.Length > 0) Row("Montato l'ultima volta su", i.LastMountedOn);
        Row("Modalità", i.ReadWrite ? "lettura/scrittura" : "sola lettura");
        if (i.RwBlocker.Length > 0) Row("Scrittura impossibile", i.RwBlocker);
        if (i.Warnings.Length > 0) Row("Note", i.Warnings);
        if (source is not null) Row("Dispositivo", source.Device + (source.Offset > 0 ? $" @ {source.Offset}" : ""));
        Row("Motore", Ext4Volume.Version);
        Body.Children.Add(grid);
        AddButton("Chiudi", true, (_, _) => DialogResult = true).IsCancel = true;
    }
}
