using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinExt4.UI;

/// <summary>Renders a <see cref="PreviewData"/>; used by the side pane and the quick-look window.</summary>
public sealed class PreviewView : DockPanel
{
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11.5 };
    private readonly CheckBox _wrap = new() { Content = "A capo automatico", Margin = new Thickness(0, 6, 0, 0), IsChecked = true };
    private readonly TextBox _text = new()
    {
        IsReadOnly = true,
        BorderThickness = new Thickness(0),
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 12,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.Wrap,
        Background = Brushes.Transparent,
    };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _message = new()
    {
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        FontSize = 13,
    };
    private readonly Grid _content = new();

    public PreviewView()
    {
        var muted = (Brush)Application.Current.FindResource("MutedBrush");
        _subtitle.Foreground = muted;
        _note.Foreground = muted;
        _message.Foreground = muted;
        _wrap.Foreground = muted;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(_title);
        header.Children.Add(_subtitle);
        header.Children.Add(_note);
        header.Children.Add(_wrap);
        SetDock(header, Dock.Top);
        Children.Add(header);

        _content.Children.Add(_text);
        _content.Children.Add(_image);
        _content.Children.Add(_message);
        Children.Add(_content);

        _wrap.Checked += (_, _) => _text.TextWrapping = TextWrapping.Wrap;
        _wrap.Unchecked += (_, _) => _text.TextWrapping = TextWrapping.NoWrap;
        Show(null);
    }

    public bool HasImage => _image.Visibility == Visibility.Visible;

    /// <summary>Lets the quick-look window show pictures bigger than their pixels.</summary>
    public bool AllowUpscale
    {
        set => _image.StretchDirection = value ? StretchDirection.Both : StretchDirection.DownOnly;
    }

    public void ShowLoading(string name)
    {
        Show(new PreviewData { Kind = PreviewKind.Message, Title = name, Text = "Caricamento anteprima…" });
    }

    public void Show(PreviewData? d)
    {
        _title.Text = d?.Title ?? "";
        _subtitle.Text = d?.Subtitle ?? "";
        _note.Text = d?.Note ?? "";
        _note.Visibility = string.IsNullOrEmpty(d?.Note) ? Visibility.Collapsed : Visibility.Visible;

        var kind = d?.Kind ?? PreviewKind.None;
        bool text = kind is PreviewKind.Text or PreviewKind.Hex;
        _text.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        _wrap.Visibility = kind == PreviewKind.Text ? Visibility.Visible : Visibility.Collapsed;
        _image.Visibility = kind == PreviewKind.Image ? Visibility.Visible : Visibility.Collapsed;
        _message.Visibility = text || kind == PreviewKind.Image ? Visibility.Collapsed : Visibility.Visible;

        _text.TextWrapping = kind == PreviewKind.Hex || _wrap.IsChecked != true ? TextWrapping.NoWrap : TextWrapping.Wrap;
        _text.Text = text ? d!.Text ?? "" : "";
        _text.ScrollToHome();
        _image.Source = kind == PreviewKind.Image ? d!.Image : null;
        _message.Text = kind == PreviewKind.None ? "Seleziona un file per vederne l'anteprima." : d?.Text ?? "";
    }
}
