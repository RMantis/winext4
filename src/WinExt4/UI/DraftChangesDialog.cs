using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinExt4.Core;

namespace WinExt4.UI;

/// <summary>What a draft changed compared with its source.</summary>
public sealed class DraftChangesDialog : SimpleDialog
{
    private sealed record Row(string Kind, string Path, string Size);

    public DraftChangesDialog(List<DraftDiff.Change> changes, DraftInfo? info) : base("Modifiche della bozza", 720)
    {
        int added = changes.Count(c => c.Kind == DraftDiff.Kind.Added);
        int removed = changes.Count(c => c.Kind == DraftDiff.Kind.Removed);
        int modified = changes.Count - added - removed;
        Body.Children.Add(DialogUi.Label(changes.Count == 0
            ? "Nessuna differenza nei file rispetto alla sorgente."
            : $"{added} aggiunti · {modified} modificati · {removed} eliminati", bold: true));
        if (info is not null)
            Body.Children.Add(DialogUi.Label(
                $"{info.Path} · creata il {info.Created:g} · {Format.Size(info.ChangedBytes)} di blocchi modificati", false));

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Modifica", Width = 100, DisplayMemberBinding = new Binding(nameof(Row.Kind)) });
        grid.Columns.Add(new GridViewColumn { Header = "Percorso", Width = 440, DisplayMemberBinding = new Binding(nameof(Row.Path)) });
        grid.Columns.Add(new GridViewColumn { Header = "Dimensione", Width = 100, DisplayMemberBinding = new Binding(nameof(Row.Size)) });
        var list = new ListView
        {
            View = grid,
            Height = 380,
            Margin = new Thickness(0, 8, 0, 0),
            ItemsSource = changes.Select(c => new Row(
                c.Kind switch { DraftDiff.Kind.Added => "Aggiunto", DraftDiff.Kind.Removed => "Eliminato", _ => "Modificato" },
                c.Path + (c.IsDirectory ? "/" : ""),
                c.IsDirectory ? "" : Format.Size(c.Size))).ToList(),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(list, "DraftChangesList");
        Body.Children.Add(list);

        string text = string.Join("\n", changes.Select(c =>
            $"{(c.Kind == DraftDiff.Kind.Added ? '+' : c.Kind == DraftDiff.Kind.Removed ? '-' : '~')} {c.Path}{(c.IsDirectory ? "/" : "")}"));
        AddButton("Copia elenco", false, (_, _) => Clipboard.SetText(text));
        AddButton("Chiudi", true, (_, _) => DialogResult = true).IsCancel = true;
    }
}
