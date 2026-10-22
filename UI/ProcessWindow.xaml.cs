using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>Un processus de la machine distante.</summary>
public sealed class ProcessRow
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string User { get; init; } = "";
}

/// <summary>
/// Gestionnaire de processus distant. L'agent renvoie la liste via un message
/// « ps » ; l'arrêt passe par la commande console « kill ».
/// </summary>
public partial class ProcessWindow : ChromeWindow
{
    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;
    private List<ProcessRow> _all = new();

    public ProcessWindow(MeshServerConnection connection, MeshNode node)
    {
        _connection = connection;
        _node = node;

        InitializeComponent();
        TitleText.Text = $"Processus — {node.Name}";

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        SetStatus("Lecture de la liste des processus…");
        try
        {
            var json = await _connection.GetProcessListAsync(_node.Id);
            _all = Parse(json);
            ApplyFilter();
            SetStatus("");
            Log.Info($"{_all.Count} processus lus sur {_node.Name}", "Processus");
        }
        catch (Exception ex)
        {
            Log.Warning($"Lecture des processus impossible : {ex.Message}", "Processus");
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
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

            rows.Add(new ProcessRow
            {
                Pid = pid,
                Name = Text(value, "cmd") ?? Text(value, "name") ?? "(inconnu)",
                User = Text(value, "user") ?? Text(value, "username") ?? "",
            });
        }

        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() : null;

    // MARK: - Filtrage

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = string.IsNullOrEmpty(FilterBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = FilterBox.Text?.Trim() ?? "";
        var rows = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || r.Pid.ToString().Contains(query))
                  .ToList();

        ProcessList.ItemsSource = rows;
        CountText.Text = rows.Count == _all.Count
            ? $"{_all.Count} processus"
            : $"{rows.Count} affichés sur {_all.Count}";
    }

    // MARK: - Actions

    private void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => KillButton.IsEnabled = ProcessList.SelectedItem is ProcessRow;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is not ProcessRow row) return;

        // Arrêter un processus système peut rendre la machine instable : la
        // confirmation nomme explicitement la cible.
        var confirm = MessageBox.Show(
            $"Arrêter « {row.Name} » (PID {row.Pid}) sur {_node.Name} ?\n\n"
          + "Le processus sera terminé sans enregistrer son travail. "
          + "Arrêter un processus système peut rendre la machine instable.",
            "Arrêter le processus ?",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            await _connection.KillProcessAsync(_node.Id, row.Pid);
            Log.Info($"Arrêt du processus {row.Pid} ({row.Name}) sur {_node.Name}", "Processus");
            SetStatus($"Ordre d'arrêt envoyé pour « {row.Name} ».");

            // L'agent n'accuse pas réception : on relit la liste pour vérifier.
            await Task.Delay(900);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Arrêt du processus {row.Pid} impossible", ex, "Processus");
            SetStatus($"Échec : {ex.Message}", isError: true);
        }
    }

    private void SetStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
    }
}
