using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using WinExt4.Core;
using WinExt4.Mount;

namespace WinExt4.UI;

/// <summary>
/// Mount confirmation (with the permissions/attributes warning) and the
/// general settings share the same options for files created from Windows.
/// </summary>
public sealed class OptionsDialog : SimpleDialog
{
    private readonly AppSettings _settings;
    private readonly ComboBox? _letter;
    private readonly RadioButton _inherit, _fixed;
    private readonly TextBox _uid, _gid, _fileMode, _dirMode;
    private readonly CheckBox _hideDots, _openExplorer, _autoOpen, _startWithWindows;
    private readonly bool _wasAutostart;

    public string? SelectedDriveLetter => _letter?.SelectedItem as string;

    public OptionsDialog(AppSettings settings, bool forMount, bool readOnly)
        : base(forMount ? "Monta su Windows" : "Impostazioni", 540)
    {
        _settings = settings;
        var muted = (System.Windows.Media.Brush)Application.Current.FindResource("MutedBrush");

        if (forMount)
        {
            var warn = new Border
            {
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("WarnBrush"),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 12),
            };
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("WarnTextBrush") };
            t.Inlines.Add(new Bold(new Run("Come vengono gestiti permessi e attributi\n")));
            t.Inlines.Add(new Run(
                "• I permessi Linux (rwx, proprietario uid/gid) NON diventano permessi Windows: sull'unità chiunque su questo PC può leggere" +
                (readOnly ? "." : " e scrivere.") + "\n" +
                "• L'attributo «Sola lettura» di Windows corrisponde al permesso di scrittura del proprietario (u+w).\n" +
                "• I file e le cartelle creati da Windows ricevono proprietario e permessi indicati qui sotto.\n" +
                "• I link simbolici vengono seguiti; quelli non validi appaiono come file vuoti.\n" +
                "• Nomi con caratteri non ammessi da Windows (\\ : * ? \" < > |) appaiono con simboli sostitutivi.\n" +
                "• ext4 distingue maiuscole/minuscole: «Foto» e «foto» sono due cartelle diverse.\n" +
                "• Attributi estesi, ACL POSIX, file di dispositivo e socket non sono gestiti."));
            if (readOnly)
                t.Inlines.Add(new Bold(new Run("\n\nIl volume è in sola lettura: l'unità sarà di sola lettura. Sblocca la scrittura per modificarlo.")));
            warn.Child = t;
            Body.Children.Add(warn);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(new TextBlock { Text = "Lettera di unità:", VerticalAlignment = VerticalAlignment.Center, Width = 150 });
            _letter = new ComboBox { Width = 80, ItemsSource = MountSession.FreeDriveLetters().ToList() };
            _letter.SelectedItem = MountSession.FreeDriveLetters().Contains(settings.PreferredDriveLetter)
                ? settings.PreferredDriveLetter
                : MountSession.FreeDriveLetters().FirstOrDefault();
            row.Children.Add(_letter);
            Body.Children.Add(row);
        }

        Body.Children.Add(new TextBlock { Text = "Nuovi file e cartelle creati da Windows", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6) });
        _inherit = new RadioButton
        {
            Content = "Stesso proprietario (uid/gid) della cartella in cui vengono creati",
            IsChecked = settings.OwnerPolicy == OwnerPolicy.InheritParent,
            Margin = new Thickness(0, 2, 0, 2),
        };
        Body.Children.Add(_inherit);
        var fixedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        _fixed = new RadioButton { Content = "Proprietario fisso:  uid ", IsChecked = settings.OwnerPolicy == OwnerPolicy.Fixed, VerticalAlignment = VerticalAlignment.Center };
        _uid = new TextBox { Text = settings.FixedUid.ToString(), Width = 60 };
        _gid = new TextBox { Text = settings.FixedGid.ToString(), Width = 60 };
        fixedRow.Children.Add(_fixed);
        fixedRow.Children.Add(_uid);
        fixedRow.Children.Add(new TextBlock { Text = "  gid ", VerticalAlignment = VerticalAlignment.Center });
        fixedRow.Children.Add(_gid);
        Body.Children.Add(fixedRow);

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 2) };
        modes.Children.Add(new TextBlock { Text = "Permessi file ", VerticalAlignment = VerticalAlignment.Center });
        _fileMode = new TextBox { Text = Convert.ToString(settings.FileMode, 8), Width = 50 };
        modes.Children.Add(_fileMode);
        modes.Children.Add(new TextBlock { Text = "   cartelle ", VerticalAlignment = VerticalAlignment.Center });
        _dirMode = new TextBox { Text = Convert.ToString(settings.DirMode, 8), Width = 50 };
        modes.Children.Add(_dirMode);
        modes.Children.Add(new TextBlock { Text = "   (ottale, es. 644 / 755)", Foreground = muted, VerticalAlignment = VerticalAlignment.Center });
        Body.Children.Add(modes);

        Body.Children.Add(new TextBlock { Text = "Esplora risorse", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
        _hideDots = new CheckBox { Content = "Mostra i file che iniziano con «.» come nascosti", IsChecked = settings.HideDotFiles, Margin = new Thickness(0, 2, 0, 2) };
        _openExplorer = new CheckBox { Content = "Apri Esplora risorse dopo il montaggio", IsChecked = settings.OpenExplorerAfterMount, Margin = new Thickness(0, 2, 0, 2) };
        Body.Children.Add(_hideDots);
        Body.Children.Add(_openExplorer);

        _autoOpen = new CheckBox { Content = "Apri automaticamente (in sola lettura) i dischi ext appena collegati", IsChecked = settings.AutoOpenOnInsert, Margin = new Thickness(0, 2, 0, 2) };
        _wasAutostart = !forMount && Autostart.IsEnabled;
        _startWithWindows = new CheckBox
        {
            Content = "Avvia con Windows in background (icona nell'area di notifica, nessun prompt UAC)",
            IsChecked = _wasAutostart,
            Margin = new Thickness(0, 2, 0, 2),
        };
        if (!forMount)
        {
            Body.Children.Add(new TextBlock { Text = "Dischi", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
            Body.Children.Add(_autoOpen);
            Body.Children.Add(_startWithWindows);
        }

        AddButton(forMount ? "Monta" : "Salva", true, OnOk);
        AddButton("Annulla", false, (_, _) => DialogResult = false).IsCancel = true;
    }

    private static bool TryOctal(string s, out uint v)
    {
        v = 0;
        s = s.Trim();
        if (s.Length is 0 or > 4 || s.Any(c => c < '0' || c > '7')) return false;
        v = Convert.ToUInt32(s, 8);
        return true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryOctal(_fileMode.Text, out uint fm) || !TryOctal(_dirMode.Text, out uint dm))
        {
            MessageBox.Show(this, "I permessi vanno scritti in ottale, per esempio 644 e 755.", Title);
            return;
        }
        if (!uint.TryParse(_uid.Text, out uint uid) || !uint.TryParse(_gid.Text, out uint gid))
        {
            MessageBox.Show(this, "uid e gid devono essere numeri.", Title);
            return;
        }
        _settings.OwnerPolicy = _fixed.IsChecked == true ? OwnerPolicy.Fixed : OwnerPolicy.InheritParent;
        _settings.FixedUid = uid;
        _settings.FixedGid = gid;
        _settings.FileMode = fm;
        _settings.DirMode = dm;
        _settings.HideDotFiles = _hideDots.IsChecked == true;
        _settings.OpenExplorerAfterMount = _openExplorer.IsChecked == true;
        _settings.AutoOpenOnInsert = _autoOpen.IsChecked == true;
        if (SelectedDriveLetter is { } l) _settings.PreferredDriveLetter = l;
        bool autostart = _startWithWindows.IsChecked == true;
        if (_letter is null && autostart != _wasAutostart)
        {
            try
            {
                if (autostart) Autostart.Enable(); else Autostart.Disable();
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        DialogResult = true;
    }
}
