using System.Windows;
using System.Windows.Controls;
using WinExt4.Core;
using WinExt4.Native;

namespace WinExt4.UI;

/// <summary>Linux metadata of one entry; permissions and owner are editable in RW mode.</summary>
public sealed class PropertiesDialog : SimpleDialog
{
    private readonly Ext4Volume _vol;
    private readonly EntryItem _item;
    private readonly CheckBox[] _bits = new CheckBox[12];
    private readonly TextBox _octal, _uid, _gid;
    private bool _syncing;

    public PropertiesDialog(Ext4Volume vol, EntryItem item, string dirPath) : base($"Proprietà di {item.Entry.Name}", 470)
    {
        _vol = vol;
        _item = item;
        var st = item.Stat;
        bool rw = vol.ReadWrite;
        var muted = (System.Windows.Media.Brush)Application.Current.FindResource("MutedBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string k, UIElement v)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var a = new TextBlock { Text = k, Foreground = muted, Margin = new Thickness(0, 3, 0, 3), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(a, r);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 1);
            grid.Children.Add(a);
            grid.Children.Add(v);
        }
        UIElement Text(string s) => new TextBox { Text = s, IsReadOnly = true, BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };

        Row("Percorso", Text(dirPath == "/" ? "/" + item.Entry.Name : dirPath + "/" + item.Entry.Name));
        Row("Tipo", Text(item.TypeText));
        if (item.IsLink) Row("Punta a", Text(item.LinkTarget + (item.TargetStat is null ? "  (non valido)" : "")));
        if (!st.IsDirectory) Row("Dimensione", Text($"{Format.Size(st.Size)} ({st.Size:N0} byte)"));
        Row("Occupato su disco", Text(Format.Size(st.AllocSize)));
        Row("Inode", Text($"{st.Ino}   ·   link: {st.Links}"));
        Row("Modificato", Text(st.ModifyTime.ToLocalTime().ToString("G")));
        Row("Ultimo accesso", Text(st.AccessTime.ToLocalTime().ToString("G")));
        Row("Cambio metadati", Text(st.ChangeTime.ToLocalTime().ToString("G")));
        if (st.CreateTime is { } created) Row("Creato", Text(created.ToLocalTime().ToString("G")));

        var owner = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        owner.Children.Add(new TextBlock { Text = "uid ", VerticalAlignment = VerticalAlignment.Center });
        _uid = new TextBox { Text = st.Uid.ToString(), Width = 70, IsReadOnly = !rw };
        owner.Children.Add(_uid);
        owner.Children.Add(new TextBlock { Text = "   gid ", VerticalAlignment = VerticalAlignment.Center });
        _gid = new TextBox { Text = st.Gid.ToString(), Width = 70, IsReadOnly = !rw };
        owner.Children.Add(_gid);
        Row("Proprietario", owner);

        var perms = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        for (int col = 0; col < 4; col++) perms.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(col == 0 ? 80 : 60) });
        string[] who = { "Proprietario", "Gruppo", "Altri" };
        string[] what = { "lettura", "scrittura", "esecuz." };
        for (int r = 0; r < 4; r++) perms.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < 3; c++)
        {
            var h = new TextBlock { Text = what[c], Foreground = muted, FontSize = 11.5 };
            Grid.SetColumn(h, c + 1);
            perms.Children.Add(h);
        }
        for (int r = 0; r < 3; r++)
        {
            var l = new TextBlock { Text = who[r], Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(l, r + 1);
            perms.Children.Add(l);
            for (int c = 0; c < 3; c++)
            {
                int bit = 8 - (r * 3 + c);
                var cb = new CheckBox { IsChecked = (st.Mode & (1u << bit)) != 0, IsEnabled = rw, Margin = new Thickness(0, 2, 0, 2) };
                cb.Checked += (_, _) => SyncFromBits();
                cb.Unchecked += (_, _) => SyncFromBits();
                _bits[bit] = cb;
                Grid.SetRow(cb, r + 1);
                Grid.SetColumn(cb, c + 1);
                perms.Children.Add(cb);
            }
        }
        // setuid / setgid / sticky are kept but only editable via the octal box
        for (int bit = 9; bit < 12; bit++)
            _bits[bit] = new CheckBox { IsChecked = (st.Mode & (1u << bit)) != 0 };
        Row("Permessi", perms);

        _octal = new TextBox { Text = Convert.ToString(st.Mode & 0xFFF, 8).PadLeft(4, '0'), Width = 60, IsReadOnly = !rw, HorizontalAlignment = HorizontalAlignment.Left, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas") };
        _octal.TextChanged += (_, _) => SyncFromOctal();
        Row("Ottale", _octal);
        Body.Children.Add(grid);

        if (!rw)
            Body.Children.Add(new TextBlock { Text = "Sblocca la scrittura per modificare permessi e proprietario.", Foreground = muted, Margin = new Thickness(0, 8, 0, 0) });

        if (rw) AddButton("Applica", true, OnApply);
        AddButton(rw ? "Annulla" : "Chiudi", !rw, (_, _) => DialogResult = false).IsCancel = true;
    }

    private uint ModeFromBits()
    {
        uint m = 0;
        for (int i = 0; i < 12; i++)
            if (_bits[i].IsChecked == true) m |= 1u << i;
        return m;
    }

    private void SyncFromBits()
    {
        if (_syncing) return;
        _syncing = true;
        _octal.Text = Convert.ToString(ModeFromBits(), 8).PadLeft(4, '0');
        _syncing = false;
    }

    private void SyncFromOctal()
    {
        if (_syncing) return;
        string s = _octal.Text.Trim();
        if (s.Length is 0 or > 4 || s.Any(ch => ch < '0' || ch > '7')) return;
        uint m = Convert.ToUInt32(s, 8);
        _syncing = true;
        for (int i = 0; i < 12; i++) _bits[i].IsChecked = (m & (1u << i)) != 0;
        _syncing = false;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!uint.TryParse(_uid.Text, out uint uid) || !uint.TryParse(_gid.Text, out uint gid))
        {
            MessageBox.Show(this, "uid e gid devono essere numeri.", Title);
            return;
        }
        uint mode = ModeFromBits();
        var st = _item.Stat;
        uint mask = 0;
        if (mode != st.Permissions) mask |= Ext4Native.SetMode;
        if (uid != st.Uid) mask |= Ext4Native.SetUid;
        if (gid != st.Gid) mask |= Ext4Native.SetGid;
        if (mask == 0)
        {
            DialogResult = false;
            return;
        }
        try
        {
            _vol.SetAttributes(st.Ino, mask, mode, uid, gid);
            _vol.Flush();
            DialogResult = true;
        }
        catch (Ext4Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
