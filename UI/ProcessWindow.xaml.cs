using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>Un processus de la machine distante.</summary>
public sealed class ProcessRow : INotifyPropertyChanged
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string User { get; init; } = "";

    public string PidText => Pid.ToString();

    /// <summary>Ligne de processus, pas un en-tête (style commun des lignes).</summary>
    public bool IsHeader => false;

    /// <summary>
    /// Processus du système ou d'un service : sans utilisateur, ou lancé par
    /// SYSTEM, un compte de service Windows, ou root. On les arrête rarement,
    /// et rarement sans conséquence : ils s'affichent en retrait.
    /// </summary>
    public bool IsSystem { get; init; }

    public string UserLabel => string.IsNullOrEmpty(User) ? "Système" : User;

    /// <summary>Pictogramme : engrenage pour le système, fenêtre pour un programme d'utilisateur.</summary>
    public string Glyph => IsSystem ? "" : "";
    public string GlyphColor => Palette.Adapt(IsSystem ? "#8A8A8E" : "#0A84FF");

    private bool _isStopping;
    /// <summary>Arrêt demandé, en attente de confirmation par une relecture de la liste.</summary>
    public bool IsStopping
    {
        get => _isStopping;
        set
        {
            if (_isStopping == value) return;
            _isStopping = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStopping)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static readonly string[] SystemAccounts =
        ["SYSTEM", "SYSTÈME", "AUTORITE NT", "AUTORITÉ NT", "NT AUTHORITY", "SERVICE LOCAL", "LOCAL SERVICE",
         "SERVICE RÉSEAU", "NETWORK SERVICE", "DWM-", "UMFD-", "FONT DRIVER HOST", "WINDOW MANAGER", "ROOT"];

    public static bool LooksLikeSystem(string user)
        => string.IsNullOrWhiteSpace(user)
           || SystemAccounts.Any(a => user.Contains(a, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Gestionnaire de processus distant. L'agent renvoie la liste via un message
/// « ps » ; l'arrêt passe par « pskill », comme l'interface web de MeshCentral,
/// puis par un arrêt forcé si le processus résiste. Chaque arrêt est vérifié
/// en relisant la liste : on dit ce qui a réellement été arrêté.
/// </summary>
public partial class ProcessWindow : ChromeWindow
{
    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;
    private List<ProcessRow> _all = new();

    /// <summary>Pastille active : tous, utilisateurs ou système.</summary>
    private string _scope = "all";

    private string _sortColumn = "name";
    private bool _sortDescending;

    private readonly DispatcherTimer _autoRefresh = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _loading;

    public ProcessWindow(MeshServerConnection connection, MeshNode node)
    {
        _connection = connection;
        _node = node;

        InitializeComponent();
        TitleText.Text = $"Processus de {node.Name}";
        NodeDot.Visibility = node.IsOnline ? Visibility.Visible : Visibility.Collapsed;
        UpdateSortArrows();
        ColumnHeaderAlign.Attach(ProcessList, scrollbar => ColumnHeader.Padding = new Thickness(10, 4, 10 + scrollbar, 7));

        _autoRefresh.Tick += async (_, _) => await LoadAsync(quiet: true);
        Closed += (_, _) => _autoRefresh.Stop();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) { _ = LoadAsync(); e.Handled = true; }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                FilterBox.Focus();
                FilterBox.SelectAll();
                e.Handled = true;
            }
        };

        Loaded += async (_, _) =>
        {
            FilterBox.Focus();
            await LoadAsync();
        };
    }

    // MARK: - Lecture

    /// <summary>
    /// Relit la liste. <paramref name="quiet"/> : actualisation automatique,
    /// sans indicateur de chargement ni message d'erreur envahissant.
    /// </summary>
    private async Task<bool> LoadAsync(bool quiet = false)
    {
        if (_loading) return false;
        _loading = true;
        RefreshButton.IsEnabled = false;
        if (!quiet && _all.Count == 0) LoadingState.Visibility = Visibility.Visible;
        try
        {
            var json = await _connection.GetProcessListAsync(_node.Id);
            var stopping = _all.Where(r => r.IsStopping).Select(r => r.Pid).ToHashSet();
            _all = Parse(json);
            foreach (var row in _all.Where(r => stopping.Contains(r.Pid))) row.IsStopping = true;
            ApplyFilter();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"Lecture des processus impossible : {ex.Message}", "Processus");
            if (!quiet) ShowStatus(ex.Message, StatusKind.Error);
            return false;
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
            LoadingState.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// L'agent renvoie un objet { "1234": { "pid":1234, "cmd":"...", "user":"..." } }.
    /// Les noms de champs varient selon la plateforme : on accepte plusieurs formes.
    /// </summary>
    private static List<ProcessRow> Parse(string json)
    {
        var rows = new List<ProcessRow>();
        if (string.IsNullOrWhiteSpace(json)) return rows;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return rows;

        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var value = entry.Value;
            if (value.ValueKind != JsonValueKind.Object) continue;

            int pid = int.TryParse(entry.Name, out var parsed) ? parsed : 0;
            if (value.TryGetProperty("pid", out var pidEl) && pidEl.ValueKind == JsonValueKind.Number)
                pid = pidEl.GetInt32();

            var user = Text(value, "user") ?? Text(value, "username") ?? "";
            rows.Add(new ProcessRow
            {
                Pid = pid,
                Name = Text(value, "cmd") ?? Text(value, "name") ?? "(inconnu)",
                User = user,
                IsSystem = ProcessRow.LooksLikeSystem(user),
            });
        }

        return rows;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() : null;

    // MARK: - Filtre, pastilles et tri

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = string.IsNullOrEmpty(FilterBox.Text);
        FilterPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearFilterButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilter();
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FilterBox.Text.Length > 0)
        {
            FilterBox.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && ProcessList.Items.Count > 0)
        {
            ProcessList.SelectedIndex = 0;
            (ProcessList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        FilterBox.Clear();
        FilterBox.Focus();
    }

    private void SearchFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FilterBox.IsKeyboardFocusWithin) return;
        FilterBox.Focus();
        FilterBox.CaretIndex = FilterBox.Text.Length;
        e.Handled = true;
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string scope } chip) return;
        _scope = scope;
        Motion.Pop(chip);
        ApplyFilter();
    }

    private void Header_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string column }) return;
        // Un second clic sur la même colonne inverse l'ordre.
        _sortDescending = column == _sortColumn && !_sortDescending;
        _sortColumn = column;
        UpdateSortArrows();
        ApplyFilter();
    }

    private void UpdateSortArrows()
    {
        // Segoe MDL2 Assets : E70D chevron bas (croissant), E70E chevron haut.
        var arrow = _sortDescending ? "" : "";
        ArrowName.Text = _sortColumn == "name" ? arrow : "";
        ArrowPid.Text = _sortColumn == "pid" ? arrow : "";
        ArrowUser.Text = _sortColumn == "user" ? arrow : "";
    }

    private void ApplyFilter()
    {
        var query = FilterBox.Text?.Trim() ?? "";
        var selectedPids = ProcessList.SelectedItems.OfType<ProcessRow>().Select(r => r.Pid).ToHashSet();

        // Pastilles : comptées sur la recherche en cours.
        var matching = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(r => TextMatch.Contains(r.Name, query)
                              || r.PidText.Contains(query, StringComparison.Ordinal)
                              || TextMatch.Contains(r.UserLabel, query)).ToList();
        ChipAllCount.Text = matching.Count.ToString();
        ChipUserCount.Text = matching.Count(r => !r.IsSystem).ToString();
        ChipSystemCount.Text = matching.Count(r => r.IsSystem).ToString();
        ChipAll.IsChecked = _scope == "all";
        ChipUser.IsChecked = _scope == "user";
        ChipSystem.IsChecked = _scope == "system";

        IEnumerable<ProcessRow> rows = _scope switch
        {
            "user" => matching.Where(r => !r.IsSystem),
            "system" => matching.Where(r => r.IsSystem),
            _ => matching,
        };

        rows = _sortColumn switch
        {
            "pid" => _sortDescending ? rows.OrderByDescending(r => r.Pid) : rows.OrderBy(r => r.Pid),
            "user" => _sortDescending
                ? rows.OrderByDescending(r => r.UserLabel, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.UserLabel, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => _sortDescending
                ? rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Pid)
                : rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Pid),
        };

        var list = rows.ToList();
        Highlight.SetQuery(ProcessList, query);
        ProcessList.ItemsSource = list;
        foreach (var row in list.Where(r => selectedPids.Contains(r.Pid)))
            ProcessList.SelectedItems.Add(row);

        CountText.Text = list.Count == _all.Count
            ? $"{_all.Count} processus"
            : $"{list.Count} affichés sur {_all.Count} processus";
        EmptyState.Visibility = list.Count == 0 && _all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateKillButton();
    }

    // MARK: - Sélection et raccourcis

    private List<ProcessRow> SelectedRows => ProcessList.SelectedItems.OfType<ProcessRow>()
        .Where(r => !r.IsStopping).ToList();

    private void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateKillButton();

    private void UpdateKillButton()
    {
        var count = SelectedRows.Count;
        KillButton.IsEnabled = count > 0;
        KillLabel.Text = count > 1 ? $"Arrêter ({count})" : "Arrêter";
        MenuKill.Header = count > 1 ? $"Arrêter les {count} processus" : "Arrêter";
    }

    private void ProcessList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && SelectedRows.Count > 0)
        {
            _ = KillAsync(SelectedRows);
            e.Handled = true;
        }
    }

    /// <summary>Le clic droit sélectionne la ligne visée, sauf si elle l'est déjà.</summary>
    private void ProcessList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ProcessList, (DependencyObject)e.OriginalSource)
                is ListBoxItem { IsSelected: false } item)
        {
            ProcessList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void CopyName_Click(object sender, RoutedEventArgs e) => Copy(string.Join(Environment.NewLine, SelectedRows.Select(r => r.Name)));
    private void CopyPid_Click(object sender, RoutedEventArgs e) => Copy(string.Join(Environment.NewLine, SelectedRows.Select(r => r.PidText)));

    private static void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { /* presse-papiers occupé */ }
    }

    private void FilterSameName_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is not { } row) return;
        FilterBox.Text = row.Name;
        FilterBox.CaretIndex = FilterBox.Text.Length;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void AutoRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshToggle.IsChecked == true) _autoRefresh.Start();
        else _autoRefresh.Stop();
    }

    // MARK: - Arrêt

    private async void Kill_Click(object sender, RoutedEventArgs e) => await KillAsync(SelectedRows);

    private async void RowKill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProcessRow row } || row.IsStopping) return;
        await KillAsync([row]);
    }

    /// <summary>
    /// Arrête des processus, puis vérifie : « pskill » d'abord ; ceux qui
    /// tournent encore une seconde et demie plus tard sont arrêtés de force
    /// (taskkill /F, kill -9) ; un dernier relevé dit ce qui a réellement été
    /// arrêté et ce que la machine a refusé.
    /// </summary>
    private async Task KillAsync(IReadOnlyList<ProcessRow> rows)
    {
        if (rows.Count == 0) return;

        var names = string.Join("\n", rows.Take(8).Select(r => $"• {r.Name} (PID {r.Pid})"))
                    + (rows.Count > 8 ? $"\n… et {rows.Count - 8} autres" : "");
        var system = rows.Any(r => r.IsSystem)
            ? "\n\nAttention : la sélection contient des processus du système ou de services ; "
              + "les arrêter peut rendre la machine instable."
            : "";
        var title = rows.Count == 1 ? "Arrêter le processus ?" : $"Arrêter {rows.Count} processus ?";
        if (MessageBox.Show(this,
                $"{names}\n\nSur « {_node.Name} ». Les processus seront terminés sans enregistrer leur travail.{system}",
                title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var pids = rows.Select(r => r.Pid).ToHashSet();
        foreach (var row in rows) row.IsStopping = true;
        UpdateKillButton();
        ShowStatus(rows.Count == 1 ? $"Arrêt de « {rows[0].Name} »…" : $"Arrêt de {rows.Count} processus…",
                   StatusKind.Progress);

        try
        {
            foreach (var pid in pids) await _connection.KillProcessAsync(_node.Id, pid);
            Log.Info($"Arrêt demandé sur {_node.Name} : {string.Join(", ", rows.Select(r => $"{r.Name} ({r.Pid})"))}", "Processus");

            var remaining = await StillRunningAsync(pids, delayMs: 1500);
            if (remaining.Count > 0)
            {
                // Résistance : arrêt forcé par la machine elle-même.
                var windows = _node.OsFamily == OSFamily.Windows;
                foreach (var pid in remaining) await _connection.ForceKillProcessAsync(_node.Id, pid, windows);
                Log.Info($"Arrêt forcé demandé pour {string.Join(", ", remaining)}", "Processus");
                remaining = await StillRunningAsync(remaining, delayMs: 2500);
            }

            var stopped = rows.Where(r => !remaining.Contains(r.Pid)).ToList();
            var refused = rows.Where(r => remaining.Contains(r.Pid)).ToList();
            foreach (var row in _all.Where(r => remaining.Contains(r.Pid))) row.IsStopping = false;
            ApplyFilter();

            if (refused.Count == 0)
            {
                ShowStatus(stopped.Count == 1 ? $"« {stopped[0].Name} » est arrêté." : $"{stopped.Count} processus arrêtés.",
                           StatusKind.Success);
            }
            else
            {
                var list = string.Join(", ", refused.Take(4).Select(r => $"« {r.Name} »"));
                ShowStatus($"{list} {(refused.Count == 1 ? "tourne" : "tournent")} toujours : la machine a refusé l'arrêt "
                         + "(processus protégé, ou droits insuffisants sur ce groupe d'appareils)."
                         + (stopped.Count > 0 ? $" {stopped.Count} autre(s) arrêté(s)." : ""),
                           StatusKind.Error);
                Log.Warning($"Arrêt refusé sur {_node.Name} : {string.Join(", ", refused.Select(r => r.Pid))}", "Processus");
            }
        }
        catch (Exception ex)
        {
            foreach (var row in rows) row.IsStopping = false;
            Log.Error("Arrêt de processus impossible", ex, "Processus");
            ShowStatus($"Échec : {ex.Message}", StatusKind.Error);
        }
        UpdateKillButton();
    }

    /// <summary>Relit la liste après un délai et rend les PID encore présents.</summary>
    private async Task<HashSet<int>> StillRunningAsync(IReadOnlyCollection<int> pids, int delayMs)
    {
        await Task.Delay(delayMs);
        // Une lecture déjà en cours (actualisation automatique) : on attend qu'elle finisse.
        for (var i = 0; i < 20 && _loading; i++) await Task.Delay(100);
        if (!await LoadAsync(quiet: true)) return pids.ToHashSet();
        var present = _all.Select(r => r.Pid).ToHashSet();
        return pids.Where(present.Contains).ToHashSet();
    }

    // MARK: - Compte rendu

    private enum StatusKind { Progress, Success, Error }

    private void ShowStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        (StatusGlyph.Text, var glyphBrush, var background) = kind switch
        {
            StatusKind.Success => ("", "OnlineBrush", "SubtleFillBrush"),
            StatusKind.Error => ("", "DangerBrush", "DangerSoftBrush"),
            _ => ("", "AccentBrush", "SubtleFillBrush"),
        };
        StatusGlyph.Foreground = (Brush)FindResource(glyphBrush);
        StatusBanner.Background = (Brush)FindResource(background);
        if (StatusBanner.Visibility != Visibility.Visible)
        {
            StatusBanner.Visibility = Visibility.Visible;
            Motion.FadeIn(StatusBanner, 0, 180, slideY: 6);
        }
    }
}
