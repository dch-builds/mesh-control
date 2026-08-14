using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>
/// Palette de recherche rapide, ouverte par le raccourci global. On tape un nom
/// de machine, Entrée ouvre le bureau à distance — sans quitter l'application en
/// cours d'utilisation.
/// </summary>
public partial class QuickSearchWindow : Window
{
    private readonly List<DeviceRow> _rows;
    private readonly Action<MeshNode> _onConnect;

    /// <summary>
    /// Empêche une double fermeture : Close() retire le focus, ce qui relance
    /// Deactivated → Close() sur une fenêtre déjà en cours de fermeture (ce que
    /// WPF rejette par une InvalidOperationException).
    /// </summary>
    private bool _closing;

    public QuickSearchWindow(IEnumerable<DeviceRow> rows, Action<MeshNode> onConnect)
    {
        InitializeComponent();
        _rows = rows.ToList();
        _onConnect = onConnect;

        // Les machines joignables d'abord : ce sont celles auxquelles on veut se
        // connecter dans l'immédiat.
        UpdateResults("");

        // La palette est ouverte alors qu'une AUTRE application a le focus :
        // Windows bride l'activation dans ce cas, il faut donc forcer la mise au
        // premier plan puis donner explicitement le focus au champ de saisie.
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
        };

        // Se referme dès qu'on clique ailleurs : comportement attendu d'une palette.
        Deactivated += (_, _) => DismissSafely();
    }

    /// <summary>Ferme la palette une seule fois, sans risque de réentrance.</summary>
    private void DismissSafely()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = string.IsNullOrEmpty(QueryBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateResults(QueryBox.Text);
    }

    private void UpdateResults(string query)
    {
        query = query.Trim();

        IEnumerable<DeviceRow> matches = _rows;
        if (!string.IsNullOrEmpty(query))
        {
            matches = _rows
                .Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || r.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase))
                // Les correspondances en début de nom passent devant.
                .OrderByDescending(r => r.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => r.IsOnline)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            matches = _rows.OrderByDescending(r => r.IsOnline)
                           .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        }

        var list = matches.Take(50).ToList();   // au-delà, affiner la recherche est plus rapide
        ResultList.ItemsSource = list;
        if (list.Count > 0) ResultList.SelectedIndex = 0;

        HintText.Text = list.Count switch
        {
            0 => "Aucune machine ne correspond.",
            1 => "1 résultat · Entrée pour se connecter",
            _ => $"{list.Count} résultats · ↑↓ naviguer · Entrée se connecter · Échap fermer",
        };
    }

    private void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                DismissSafely();
                e.Handled = true;
                break;

            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                ConnectToSelection();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (ResultList.Items.Count == 0) return;
        int index = ResultList.SelectedIndex + delta;
        index = Math.Clamp(index, 0, ResultList.Items.Count - 1);
        ResultList.SelectedIndex = index;
        ResultList.ScrollIntoView(ResultList.SelectedItem);
    }

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => ConnectToSelection();

    private void ConnectToSelection()
    {
        if (ResultList.SelectedItem is not DeviceRow row) return;

        // On ferme d'abord : la fenêtre de bureau à distance prend le relais et
        // reçoit le focus proprement.
        var node = row.Node;
        DismissSafely();
        _onConnect(node);
    }
}
