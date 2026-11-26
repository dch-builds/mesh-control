using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>
/// Palette de recherche rapide, ouverte par le raccourci global. On tape un nom
/// de machine, Entrée ouvre le bureau à distance — sans quitter l'application en
/// cours d'utilisation.
///
/// Deux étapes : choix de la machine, puis choix de l'action (les mêmes que le
/// clic droit dans la liste principale). Entrée sur une machine ouvre
/// directement le bureau à distance, le cas courant ; Tab ou → déroule les
/// autres actions.
/// </summary>
public partial class QuickSearchWindow : Window
{
    private readonly List<DeviceRow> _rows;

    /// <summary>Dernières machines utilisées, la plus récente d'abord.</summary>
    private readonly List<DeviceRow> _recent;
    private readonly Action<MeshNode, DeviceAction> _onAction;
    private readonly Func<MeshNode, DeviceAction, bool> _isAvailable;
    private readonly Func<MeshNode, bool> _isFavorite;

    /// <summary>Machine choisie ; non nul quand la palette affiche ses actions.</summary>
    private MeshNode? _target;

    /// <summary>
    /// Empêche une double fermeture : Close() retire le focus, ce qui relance
    /// Deactivated → Close() sur une fenêtre déjà en cours de fermeture (ce que
    /// WPF rejette par une InvalidOperationException).
    /// </summary>
    private bool _closing;

    public QuickSearchWindow(IEnumerable<DeviceRow> rows,
                             Action<MeshNode, DeviceAction> onAction,
                             Func<MeshNode, DeviceAction, bool> isAvailable,
                             Func<MeshNode, bool> isFavorite,
                             IEnumerable<string>? recentNodeIds = null)
    {
        InitializeComponent();
        _rows = rows.ToList();

        // Copies marquées « Récente » : la ligne d'origine reste telle quelle
        // dans le reste de la liste et dans la fenêtre principale.
        var byId = _rows.GroupBy(r => r.Node.Id).ToDictionary(g => g.Key, g => g.First());
        _recent = (recentNodeIds ?? [])
            .Distinct()
            .Select(id => byId.GetValueOrDefault(id))
            .OfType<DeviceRow>()
            .Select(r => new DeviceRow(r.Node, r.RealGroupName, r.IsFavorite) { IsRecent = true })
            .ToList();
        _onAction = onAction;
        _isAvailable = isAvailable;
        _isFavorite = isFavorite;

        // Les dernières connexions d'abord, puis les machines joignables : ce
        // sont celles auxquelles on veut se connecter dans l'immédiat.
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

        if (_target is null) UpdateResults(QueryBox.Text);
        else UpdateActions(QueryBox.Text);
    }

    // MARK: - Étape 1 : choix de la machine

    private void UpdateResults(string query)
    {
        query = query.Trim();

        IEnumerable<DeviceRow> matches;
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
            // Sans saisie : les dernières connexions en tête, dans l'ordre
            // d'utilisation, puis les autres machines sans doublon.
            var recentIds = _recent.Select(r => r.Node.Id).ToHashSet();
            matches = _recent.Concat(
                _rows.Where(r => !recentIds.Contains(r.Node.Id))
                     .OrderByDescending(r => r.IsOnline)
                     .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase));
        }

        var list = matches.Take(50).ToList();   // au-delà, affiner la recherche est plus rapide
        ResultList.ItemsSource = list;
        if (list.Count > 0) ResultList.SelectedIndex = 0;

        HintText.Text = list.Count switch
        {
            0 => "Aucune machine ne correspond.",
            1 => "1 résultat · Entrée bureau · Ctrl+Maj+W page web · Tab autres actions",
            _ when string.IsNullOrEmpty(query) && _recent.Count > 0
                => "Dernières connexions en tête · ↑↓ naviguer · Entrée bureau · Tab actions",
            _ => $"{list.Count} résultats · ↑↓ naviguer · Entrée bureau · Ctrl+Maj+W page web · Tab actions",
        };
    }

    // MARK: - Étape 2 : choix de l'action

    /// <summary>
    /// Bascule vers la liste des actions applicables à la machine sélectionnée.
    /// </summary>
    private void ShowActionsFor(MeshNode node)
    {
        _target = node;
        ContextLabel.Text = node.Name;
        ContextPanel.Visibility = Visibility.Visible;
        Placeholder.Text = "Rechercher une action…";

        QueryBox.Clear();               // déclenche UpdateActions("") via TextChanged
        Placeholder.Visibility = Visibility.Visible;
        UpdateActions("");
    }

    /// <summary>Revient à la liste des machines.</summary>
    private void BackToDevices()
    {
        _target = null;
        ContextPanel.Visibility = Visibility.Collapsed;
        Placeholder.Text = "Nom d'une machine…";

        QueryBox.Clear();
        Placeholder.Visibility = Visibility.Visible;
        UpdateResults("");
    }

    private void UpdateActions(string query)
    {
        if (_target is not { } node) return;
        query = query.Trim();

        var items = DeviceActionInfo.All
            .Select(info => new ActionRow(info, node, _isAvailable(node, info.Action), _isFavorite(node)));

        if (!string.IsNullOrEmpty(query))
        {
            items = items
                .Where(a => a.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || Normalize(a.Info.Keywords).Contains(Normalize(query)))
                .OrderByDescending(a => a.Label.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        }

        // Les actions indisponibles restent visibles, en grisé : mieux vaut voir
        // pourquoi une action manque que de la chercher dans une liste tronquée.
        var list = items.ToList();
        ResultList.ItemsSource = list;

        var first = list.FindIndex(a => a.IsAvailable);
        ResultList.SelectedIndex = first >= 0 ? first : (list.Count > 0 ? 0 : -1);

        HintText.Text = list.Count == 0
            ? "Aucune action ne correspond."
            : $"{node.Name} · Entrée exécuter · Échap ou ← revenir aux machines";
    }

    /// <summary>
    /// Comparaison insensible aux accents : « systeme » doit trouver « système ».
    /// </summary>
    private static string Normalize(string text)
    {
        var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    // MARK: - Clavier

    /// <summary>
    /// Machine visée par les raccourcis directs : celle en surbrillance tant
    /// qu'on choisit la machine, celle dont on parcourt les actions ensuite.
    /// </summary>
    private MeshNode? HighlightedNode
        => _target ?? (ResultList.SelectedItem as DeviceRow)?.Node;

    private void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Maj+W ouvre la page web MeshCentral de la machine en surbrillance
        // sans passer par la liste des actions : ce détour était ce qui coûtait
        // le plus de frappes. Même combinaison que dans le bureau à distance,
        // pour n'avoir qu'un seul geste à retenir dans toute l'application.
        if (e.Key == Key.W
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (HighlightedNode is { } node) Run(node, DeviceAction.WebPage);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                // Dans les actions, Échap revient d'abord aux machines.
                if (_target is not null) BackToDevices();
                else DismissSafely();
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

            case Key.Tab:
            case Key.Right:
                // → ne bascule que depuis la fin du texte, pour ne pas gêner le
                // déplacement normal du curseur dans la saisie.
                if (_target is null
                    && (e.Key == Key.Tab || QueryBox.CaretIndex == QueryBox.Text.Length))
                {
                    if (ResultList.SelectedItem is DeviceRow row) ShowActionsFor(row.Node);
                    e.Handled = true;
                }
                break;

            case Key.Left:
                if (_target is not null && QueryBox.CaretIndex == 0)
                {
                    BackToDevices();
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                Activate(ResultList.SelectedItem);
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
        => Activate(ResultList.SelectedItem);

    /// <summary>
    /// Valide l'élément sélectionné : une machine ouvre son bureau à distance,
    /// une action l'exécute sur la machine choisie.
    /// </summary>
    private void Activate(object? selection)
    {
        switch (selection)
        {
            case DeviceRow row:
                Run(row.Node, DeviceAction.Desktop);
                break;

            case ActionRow action when _target is { } node:
                if (!action.IsAvailable) return;    // entrée grisée : sans effet
                Run(node, action.Info.Action);
                break;
        }
    }

    private void Run(MeshNode node, DeviceAction action)
    {
        // On ferme d'abord : la fenêtre de session prend le relais et reçoit le
        // focus proprement.
        DismissSafely();
        _onAction(node, action);
    }
}

/// <summary>Une action telle qu'affichée dans la palette.</summary>
public sealed class ActionRow
{
    public DeviceActionInfo Info { get; }
    public bool IsAvailable { get; }

    public ActionRow(DeviceActionInfo info, MeshNode node, bool available, bool isFavorite)
    {
        Info = info;
        IsAvailable = available;

        Label = info.Action == DeviceAction.ToggleFavorite
            ? (isFavorite ? "Retirer des favoris" : "Ajouter aux favoris")
            : info.Label;

        Detail = available ? info.Detail : UnavailableReason(node);
    }

    public string Label { get; }
    public string Detail { get; }
    public string Glyph => Info.Glyph;

    /// <summary>Opacité de la ligne : les actions impossibles sont estompées.</summary>
    public double RowOpacity => IsAvailable ? 1.0 : 0.4;

    private static string UnavailableReason(MeshNode node)
    {
        if (!node.IsOnline) return "Machine hors ligne";
        if (!node.HasAgent) return "Aucun agent installé";
        return "Indisponible";
    }
}
