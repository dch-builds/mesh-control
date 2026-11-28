using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeshControl.Core;
using MeshControl.UI;

namespace MeshControl.Mac;

/// <summary>
/// Palette de recherche rapide, ouverte par le raccourci global (portage de
/// UI/QuickSearchWindow.xaml.cs). On tape un nom de machine, Entrée ouvre le
/// bureau à distance — sans quitter l'application en cours d'utilisation.
///
/// Deux étapes : choix de la machine, puis choix de l'action (les mêmes que le
/// clic droit dans la liste principale). Entrée sur une machine ouvre
/// directement le bureau à distance ; Tab ou → déroule les autres actions.
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

    /// <summary>Empêche une double fermeture (Close relance Deactivated).</summary>
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

        UpdateResults("");

        // Les touches de navigation passent avant le champ de saisie.
        AddHandler(KeyDownEvent, QueryBox_KeyDown, RoutingStrategies.Tunnel);

        Opened += (_, _) =>
        {
            Activate();
            QueryBox.Focus();
        };

        // Se referme dès qu'on clique ailleurs : comportement attendu d'une palette.
        Deactivated += (_, _) => DismissSafely();
    }

    /// <summary>Ferme la palette une seule fois, sans risque de réentrance.</summary>
    public void DismissSafely()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void QueryBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = QueryBox.Text ?? "";
        Placeholder.IsVisible = text.Length == 0;
        if (_target is null) UpdateResults(text);
        else UpdateActions(text);
    }

    // MARK: - Étape 1 : choix de la machine

    private void UpdateResults(string query)
    {
        query = query.Trim();

        IEnumerable<DeviceRow> matches;
        if (!string.IsNullOrEmpty(query))
        {
            matches = _rows
                .Where(r => TextMatch.Contains(r.Name, query) || TextMatch.Contains(r.GroupName, query))
                // Les correspondances en début de nom passent devant.
                .OrderByDescending(r => r.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => r.IsOnline)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            // Sans saisie : les dernières connexions en tête, puis les autres sans doublon.
            var recentIds = _recent.Select(r => r.Node.Id).ToHashSet();
            matches = _recent.Concat(
                _rows.Where(r => !recentIds.Contains(r.Node.Id))
                     .OrderByDescending(r => r.IsOnline)
                     .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase));
        }

        var list = matches.Take(50).ToList();   // au-delà, affiner la recherche est plus rapide
        ResultList.ItemsSource = list;
        if (list.Count > 0) ResultList.SelectedIndex = 0;

        var web = OperatingSystem.IsMacOS() ? "⌃⇧W" : "Ctrl+Maj+W";
        HintText.Text = list.Count switch
        {
            0 => "Aucune machine ne correspond.",
            1 => $"1 résultat · Entrée bureau · {web} page web · Tab autres actions",
            _ when string.IsNullOrEmpty(query) && _recent.Count > 0
                => "Dernières connexions en tête · ↑↓ naviguer · Entrée bureau · Tab actions",
            _ => $"{list.Count} résultats · ↑↓ naviguer · Entrée bureau · {web} page web · Tab actions",
        };
    }

    // MARK: - Étape 2 : choix de l'action

    private void ShowActionsFor(MeshNode node)
    {
        _target = node;
        ContextLabel.Text = node.Name;
        ContextPanel.IsVisible = true;
        Placeholder.Text = "Rechercher une action…";
        QueryBox.Text = "";
        Placeholder.IsVisible = true;
        UpdateActions("");
    }

    private void BackToDevices()
    {
        _target = null;
        ContextPanel.IsVisible = false;
        Placeholder.Text = "Nom d'une machine…";
        QueryBox.Text = "";
        Placeholder.IsVisible = true;
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
                .Where(a => TextMatch.Contains(a.Label, query) || Normalize(a.Info.Keywords).Contains(Normalize(query)))
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

    /// <summary>Comparaison insensible aux accents : « systeme » doit trouver « système ».</summary>
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
    private MeshNode? HighlightedNode => _target ?? (ResultList.SelectedItem as DeviceRow)?.Node;

    private void QueryBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+Maj+W : page web MeshCentral de la machine en surbrillance, même
        // geste que dans le bureau à distance.
        if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (HighlightedNode is { } node) Run(node, DeviceAction.WebPage);
            e.Handled = true;
            return;
        }

        var text = QueryBox.Text ?? "";
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
                // → ne bascule que depuis la fin du texte, pour ne pas gêner le curseur.
                if (_target is null && (e.Key == Key.Tab || QueryBox.CaretIndex == text.Length))
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
                ActivateSelection(ResultList.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (ResultList.ItemCount == 0) return;
        var index = Math.Clamp(ResultList.SelectedIndex + delta, 0, ResultList.ItemCount - 1);
        ResultList.SelectedIndex = index;
        if (ResultList.SelectedItem is { } item) ResultList.ScrollIntoView(item);
    }

    private void ResultList_DoubleTapped(object? sender, TappedEventArgs e) => ActivateSelection(ResultList.SelectedItem);

    /// <summary>Une machine ouvre son bureau à distance, une action s'exécute sur la machine choisie.</summary>
    private void ActivateSelection(object? selection)
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
        // On ferme d'abord : la fenêtre de session prend le relais et reçoit le focus.
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
