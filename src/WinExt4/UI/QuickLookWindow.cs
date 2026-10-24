using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WinExt4.UI;

/// <summary>Large preview opened with the space bar; ← → move through the folder, Esc/space close.</summary>
public sealed class QuickLookWindow : Window
{
    private readonly IReadOnlyList<EntryItem> _items;
    private readonly Func<EntryItem, int, CancellationToken, Task<PreviewData>> _load;
    private readonly PreviewView _view = new() { AllowUpscale = false };
    private CancellationTokenSource? _cts;
    private int _index;

    public event Action<int>? IndexChanged;

    public QuickLookWindow(IReadOnlyList<EntryItem> items, int index,
        Func<EntryItem, int, CancellationToken, Task<PreviewData>> load)
    {
        _items = items;
        _index = index;
        _load = load;
        Title = "Anteprima";
        Width = 1000;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Background = Brushes.White;
        Content = new System.Windows.Controls.Border { Padding = new Thickness(16), Child = _view };
        PreviewKeyDown += OnKey;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
            case Key.Space:
                e.Handled = true;
                Close();
                break;
            case Key.Left:
            case Key.Up:
                e.Handled = true;
                await MoveAsync(-1);
                break;
            case Key.Right:
            case Key.Down:
                e.Handled = true;
                await MoveAsync(1);
                break;
        }
    }

    private async Task MoveAsync(int delta)
    {
        int next = _index + delta;
        if (next < 0 || next >= _items.Count) return;
        _index = next;
        IndexChanged?.Invoke(_index);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var item = _items[_index];
        Title = $"Anteprima — {item.Entry.Name}  ({_index + 1} di {_items.Count})";
        _view.ShowLoading(item.Entry.Name);
        int width = (int)(Math.Max(ActualWidth, 800) * VisualTreeHelper.GetDpi(this).DpiScaleX);
        try
        {
            var data = await _load(item, width, cts.Token);
            if (!cts.IsCancellationRequested) _view.Show(data);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is Core.Ext4Exception)
        {
            _view.Show(new PreviewData { Kind = PreviewKind.Message, Title = item.Entry.Name, Text = ex.Message });
        }
    }
}
