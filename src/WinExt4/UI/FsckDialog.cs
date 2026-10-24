using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinExt4.Core;

namespace WinExt4.UI;

/// <summary>Check / repair a filesystem with e4fsck, showing e2fsck's output live.</summary>
public sealed class FsckDialog : SimpleDialog
{
    private readonly Ext4Source _source;
    private readonly string? _currentDraft;
    private readonly Func<FsckMode, Task> _prepare;
    private readonly RadioButton _check, _draft, _preen, _all;
    private readonly CheckBox _force;
    private readonly TextBox _out;
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private readonly Button _start, _close, _openDraft;
    private CancellationTokenSource? _cts;
    private FsckMode _runningMode;

    /// <summary>The last run, if any.</summary>
    public FsckResult? Result { get; private set; }

    /// <summary>Set when the user asks to open the draft the repair went into.</summary>
    public string? DraftToOpen { get; private set; }

    /// <param name="currentDraft">The draft the volume is open as: repairs go there.</param>
    /// <param name="prepare">Called before each run (closes the volume when needed).</param>
    public FsckDialog(Ext4Source source, string? currentDraft, IReadOnlyList<string> reasons, Func<FsckMode, Task> prepare)
        : base("Controllo del filesystem", 760)
    {
        _source = source;
        _currentDraft = currentDraft;
        _prepare = prepare;

        Body.Children.Add(DialogUi.Label($"{source.Title} · " + (source.IsImage
            ? Path.GetFileName(source.ImagePath) + (source.PartitionNumber > 0 ? $", partizione {source.PartitionNumber}" : "")
            : $"disco {source.DiskNumber}, partizione {source.PartitionNumber}"), bold: true));
        if (reasons.Count > 0)
            Body.Children.Add(new TextBlock
            {
                Text = "Controllo consigliato: " + string.Join("; ", reasons) + ".",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.FindResource("WarnTextBrush"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        if (currentDraft is not null)
            Body.Children.Add(DialogUi.Label($"Il volume è aperto come bozza: il controllo e le riparazioni riguardano la bozza {Path.GetFileName(currentDraft)}.", false));

        var modes = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _check = Radio("Solo controllo: non modifica niente", "FsckCheck", true);
        _draft = Radio(currentDraft is null
            ? "Ripara in una bozza: la sorgente resta intatta, rivedi le modifiche e poi applicale (consigliato)"
            : "Ripara nella bozza aperta", "FsckDraft", false);
        _preen = Radio("Ripara automaticamente solo i problemi sicuri (come all'avvio di Linux, e2fsck -p)", "FsckPreen", false);
        _all = Radio("Ripara tutto direttamente sulla sorgente, rispondendo «sì» a ogni domanda (e2fsck -y)", "FsckAll", false);
        modes.Children.Add(_check);
        modes.Children.Add(_draft);
        if (currentDraft is null)
        {
            modes.Children.Add(_preen);
            modes.Children.Add(_all);
        }
        Body.Children.Add(modes);
        _force = new CheckBox
        {
            Content = new TextBlock { Text = "Controllo completo anche se il filesystem risulta pulito (-f)" },
            IsChecked = true,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Body.Children.Add(_force);

        _bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 12, 0, 0), Maximum = 1000 };
        Body.Children.Add(_bar);
        _status = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_status, "FsckStatus");
        Body.Children.Add(_status);
        _out = DialogUi.Mono("");
        _out.Height = 300;
        _out.TextWrapping = TextWrapping.NoWrap;
        _out.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _out.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _out.Margin = new Thickness(0, 6, 0, 0);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_out, "FsckOutput");
        Body.Children.Add(_out);

        _openDraft = AddButton("Apri la bozza", false, (_, _) =>
        {
            DraftToOpen = Result?.DraftPath;
            DialogResult = true;
        });
        _openDraft.Visibility = Visibility.Collapsed;
        _start = AddButton("Avvia", true, async (_, _) => await RunAsync());
        _close = AddButton("Chiudi", false, (_, _) =>
        {
            if (_cts is not null) _cts.Cancel();
            else DialogResult = Result is not null;
        });
        _close.IsCancel = true;
        Closing += (_, e) =>
        {
            // never leave a repair running behind a closed window
            if (_cts is not null) e.Cancel = true;
        };
    }

    private static RadioButton Radio(string text, string id, bool on)
    {
        var r = new RadioButton
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            IsChecked = on,
            GroupName = "fsckmode",
            Margin = new Thickness(0, 3, 0, 3),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(r, id);
        return r;
    }

    private FsckMode SelectedMode =>
        _draft.IsChecked == true ? FsckMode.RepairInDraft :
        _preen.IsChecked == true ? FsckMode.Preen :
        _all.IsChecked == true ? FsckMode.RepairAll : FsckMode.Check;

    private async Task RunAsync()
    {
        var mode = SelectedMode;
        if (mode == FsckMode.RepairAll &&
            MessageBox.Show(this,
                "La riparazione viene scritta subito sulla sorgente e non si può annullare né interrompere.\n\n" +
                "Se il disco contiene dati importanti, «Ripara in una bozza» permette di vedere prima cosa cambia.\n\nContinuare?",
                "Ripara tutto", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        string? draft = _currentDraft;
        if (draft is null && mode == FsckMode.RepairInDraft) draft = Drafts.NewPath(_source);
        if (mode is FsckMode.Preen or FsckMode.RepairAll) draft = null;

        await _prepare(mode);
        _runningMode = mode;
        _cts = new CancellationTokenSource();
        _out.Clear();
        _bar.Value = 0;
        _status.Text = "Controllo in corso…";
        _status.ClearValue(TextBlock.ForegroundProperty);
        _start.IsEnabled = _check.IsEnabled = _draft.IsEnabled = _preen.IsEnabled = _all.IsEnabled = _force.IsEnabled = false;
        _openDraft.Visibility = Visibility.Collapsed;
        // a direct repair must not be killed half-way
        bool canStop = mode is FsckMode.Check or FsckMode.RepairInDraft;
        _close.Content = canStop ? "Interrompi" : "Chiudi";
        _close.IsEnabled = canStop;
        try
        {
            Result = await Fsck.RunAsync(_source, mode, _force.IsChecked == true, draft,
                line => Dispatcher.BeginInvoke(() =>
                {
                    // the version banner comes on stderr, after stdout has started: put it first
                    if (line.StartsWith("e2fsck ", StringComparison.Ordinal) && line.EndsWith(')'))
                        _out.Text = line + Environment.NewLine + _out.Text;
                    else
                        _out.AppendText(line + Environment.NewLine);
                    _out.ScrollToEnd();
                }),
                (f, pass) => Dispatcher.BeginInvoke(() =>
                {
                    _bar.Value = f * 1000;
                    _status.Text = $"Controllo in corso: passo {pass} di 5…";
                }),
                _cts.Token);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Result = new FsckResult(8, ex.Message, mode, draft, false);
            _out.AppendText(ex.Message + Environment.NewLine);
        }
        finally
        {
            _cts = null;
        }
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background); // let queued lines land
        var r = Result!;
        if (r.Mode == FsckMode.RepairInDraft && _currentDraft is null && r.DraftPath is { } d)
        {
            if (!r.Corrected)
            {
                // nothing repaired: a new draft would only hold e2fsck's bookkeeping
                try { File.Delete(d); } catch (IOException) { }
                Result = r = r with { DraftPath = null };
            }
            else
            {
                _openDraft.Visibility = Visibility.Visible;
                _openDraft.IsDefault = true;
            }
        }
        _bar.Value = r.Failed ? _bar.Value : 1000;
        _status.Text = r.Summary;
        _status.Foreground = (Brush)Application.Current.FindResource(
            r.Clean || (r.Corrected && !r.Uncorrected) ? "SafeTextBrush" : "WarnTextBrush");
        _start.IsEnabled = _check.IsEnabled = _draft.IsEnabled = _preen.IsEnabled = _all.IsEnabled = _force.IsEnabled = true;
        _start.Content = "Ripeti";
        _close.Content = "Chiudi";
        _close.IsEnabled = true;
    }
}
