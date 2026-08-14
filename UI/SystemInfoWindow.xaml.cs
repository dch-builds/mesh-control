using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>
/// Fiche d'informations système d'une machine distante.
///
/// L'agent renvoie ces données via la commande console « sysinfo ». Selon la
/// plateforme et la version, la réponse est soit du JSON, soit du texte déjà
/// mis en forme : la lecture gère les deux cas.
/// </summary>
public partial class SystemInfoWindow : Window
{
    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;
    private string _plainText = "";

    public SystemInfoWindow(MeshServerConnection connection, MeshNode node)
    {
        _connection = connection;
        _node = node;

        InitializeComponent();
        TitleText.Text = node.Name;
        SubtitleText.Text = string.IsNullOrEmpty(node.OsDescription)
            ? "Système non renseigné" : node.OsDescription;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        SectionsPanel.Children.Clear();

        // Les informations connues du serveur s'affichent immédiatement : la
        // fenêtre n'est jamais vide, même si l'agent tarde ou ne répond pas.
        var text = new StringBuilder();
        RenderIdentity(text);
        SetStatus("Interrogation de la machine…");

        try
        {
            var response = await _connection.GetSystemInfoTextAsync(_node.Id);
            RenderAgentResponse(response, text);
            SetStatus("");
            Log.Info($"Informations système reçues de {_node.Name}", "Système");
        }
        catch (Exception ex)
        {
            Log.Warning($"Informations système indisponibles : {ex.Message}", "Système");
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            _plainText = text.ToString();
            RefreshButton.IsEnabled = true;
        }
    }

    // MARK: - Rendu

    private void RenderIdentity(StringBuilder text)
    {
        var rows = new List<(string, string)>
        {
            ("Nom", _node.Name),
            ("État", _node.IsOnline ? "En ligne" : "Hors ligne"),
            ("Système", string.IsNullOrEmpty(_node.OsDescription) ? "—" : _node.OsDescription!),
        };
        if (!string.IsNullOrEmpty(_node.Ip)) rows.Add(("Adresse IP", _node.Ip!));
        if (!string.IsNullOrEmpty(_node.Host)) rows.Add(("Hôte", _node.Host!));
        if (!string.IsNullOrEmpty(_node.Desc)) rows.Add(("Description", _node.Desc!));

        AddSection("Identité", rows, text);
    }

    /// <summary>
    /// La réponse de l'agent est soit du JSON structuré, soit du texte déjà
    /// formaté. On tente d'abord le JSON, puis on retombe sur l'affichage brut.
    /// </summary>
    private void RenderAgentResponse(string response, StringBuilder text)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            SetStatus("La machine a répondu sans contenu exploitable.", isError: true);
            return;
        }

        var trimmed = response.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                RenderJson(doc.RootElement, text);
                return;
            }
            catch { /* pas du JSON valide : affichage texte */ }
        }

        AddTextSection("Informations système", response, text);
    }

    private void RenderJson(JsonElement root, StringBuilder text)
    {
        // Structure attendue : hardware.{windows|linux|darwin}.{osinfo, memory…}
        var hardware = Find(root, "hardware");
        var platform = hardware is { } hw
            ? Find(hw, "windows") ?? Find(hw, "linux") ?? Find(hw, "darwin") ?? hw
            : root;

        RenderOperatingSystem(platform, text);
        RenderProcessor(platform, hardware, text);
        RenderMemory(platform, text);
        RenderStorage(platform, text);
        RenderHardware(platform, hardware, text);

        // Rien d'identifiable : montrer les données brutes plutôt que rien.
        if (SectionsPanel.Children.Count <= 1)
        {
            var raw = JsonSerializer.Serialize(root,
                new JsonSerializerOptions { WriteIndented = true });
            AddTextSection("Données brutes", raw, text);
        }
    }

    private void RenderOperatingSystem(JsonElement platform, StringBuilder text)
    {
        var os = Find(platform, "osinfo") ?? Find(platform, "os");
        if (os is not { } o) return;

        var rows = new List<(string, string)>();
        Add(rows, o, "Nom", "Caption", "Name", "Distribution");
        Add(rows, o, "Version", "Version");
        Add(rows, o, "Build", "BuildNumber");
        Add(rows, o, "Architecture", "OSArchitecture", "Arch");
        Add(rows, o, "Domaine", "Domain");
        Add(rows, o, "Installé le", "InstallDate");
        Add(rows, o, "Dernier démarrage", "LastBootUpTime");

        // Les dates WMI (« 20260812143000.000000+120 ») sont illisibles brutes.
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Item1 is "Installé le" or "Dernier démarrage")
                rows[i] = (rows[i].Item1, FormatWmiDate(rows[i].Item2));
        }

        AddSection("Système d'exploitation", rows, text);
    }

    private void RenderProcessor(JsonElement platform, JsonElement? hardware, StringBuilder text)
    {
        var rows = new List<(string, string)>();
        var cpu = Find(platform, "cpu") ?? (hardware is { } h ? Find(h, "cpu") : null);

        if (cpu is { ValueKind: JsonValueKind.Array } array)
        {
            int index = 0;
            foreach (var item in array.EnumerateArray())
            {
                var name = Text(item, "Name") ?? Text(item, "model") ?? "Processeur";
                var cores = Text(item, "NumberOfCores") ?? Text(item, "cores");
                var label = array.GetArrayLength() > 1 ? $"Processeur {++index}" : "Processeur";
                rows.Add((label, cores is null ? name : $"{name} — {cores} cœurs"));
            }
        }
        else if (cpu is { } single)
        {
            Add(rows, single, "Processeur", "Name", "model");
            Add(rows, single, "Cœurs", "NumberOfCores", "cores");
        }

        AddSection("Processeur", rows, text);
    }

    private void RenderMemory(JsonElement platform, StringBuilder text)
    {
        var memory = Find(platform, "memory");
        if (memory is not { } mem) return;

        var rows = new List<(string, string)>();
        if (mem.ValueKind == JsonValueKind.Array)
        {
            long total = 0;
            int slot = 0;
            foreach (var bank in mem.EnumerateArray())
            {
                var capacity = Number(bank, "Capacity") ?? Number(bank, "size");
                if (capacity is { } c) total += c;
                var speed = Text(bank, "Speed") ?? Text(bank, "ConfiguredClockSpeed");
                rows.Add(($"Barrette {++slot}",
                    capacity is { } cc
                        ? FormatBytes(cc) + (speed is null ? "" : $" — {speed} MHz")
                        : "—"));
            }
            if (total > 0) rows.Insert(0, ("Total", FormatBytes(total)));
        }
        else
        {
            Add(rows, mem, "Total", "total", "TotalPhysicalMemory");
            Add(rows, mem, "Disponible", "free", "FreePhysicalMemory");
        }

        AddSection("Mémoire", rows, text);
    }

    private void RenderStorage(JsonElement platform, StringBuilder text)
    {
        var partitions = Find(platform, "partitions") ?? Find(platform, "drives");
        if (partitions is not { ValueKind: JsonValueKind.Array } array) return;

        var rows = new List<(string, string)>();
        foreach (var part in array.EnumerateArray())
        {
            var name = Text(part, "Caption") ?? Text(part, "Name")
                       ?? Text(part, "mount") ?? "Disque";
            var size = Number(part, "Size") ?? Number(part, "size");
            var free = Number(part, "FreeSpace") ?? Number(part, "free");

            if (size is { } s && free is { } f && s > 0)
            {
                var used = (int)Math.Round(100.0 * (s - f) / s);
                rows.Add((name, $"{FormatBytes(f)} libres sur {FormatBytes(s)} ({used} % utilisé)"));
            }
            else if (size is { } onlySize)
            {
                rows.Add((name, FormatBytes(onlySize)));
            }
        }

        AddSection("Stockage", rows, text);
    }

    private void RenderHardware(JsonElement platform, JsonElement? hardware, StringBuilder text)
    {
        var rows = new List<(string, string)>();

        var identifiers = Find(platform, "identifiers")
                          ?? (hardware is { } h ? Find(h, "identifiers") : null);
        if (identifiers is { } id)
        {
            Add(rows, id, "Constructeur", "system_manufacturer", "board_vendor");
            Add(rows, id, "Modèle", "system_product_name", "product_name");
            Add(rows, id, "Numéro de série", "system_serial_number", "board_serial");
            Add(rows, id, "BIOS", "bios_version", "bios_vendor");
            Add(rows, id, "Date BIOS", "bios_date");
        }

        var bios = Find(platform, "bios") ?? (hardware is { } h2 ? Find(h2, "bios") : null);
        if (bios is { } b && rows.Count == 0)
        {
            Add(rows, b, "BIOS", "Manufacturer", "vendor");
            Add(rows, b, "Version BIOS", "Version", "version");
        }

        AddSection("Matériel", rows, text);
    }

    // MARK: - Construction visuelle

    private void AddSection(string title, List<(string Label, string Value)> rows,
                            StringBuilder text)
    {
        if (rows.Count == 0) return;

        text.AppendLine($"── {title} ──");
        var card = NewCard(title);
        var panel = (StackPanel)card.Child;

        foreach (var (label, value) in rows)
        {
            text.AppendLine($"{label} : {value}");

            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(labelBlock, 0);

            // Champ en lecture seule plutôt qu'un libellé : on copie souvent un
            // numéro de série ou une adresse.
            var valueBlock = new TextBox
            {
                Text = value,
                FontSize = 12,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            };
            Grid.SetColumn(valueBlock, 1);

            grid.Children.Add(labelBlock);
            grid.Children.Add(valueBlock);
            panel.Children.Add(grid);
        }

        text.AppendLine();
        SectionsPanel.Children.Add(card);
    }

    /// <summary>Section affichant un bloc de texte tel quel (réponse non structurée).</summary>
    private void AddTextSection(string title, string content, StringBuilder text)
    {
        text.AppendLine($"── {title} ──").AppendLine(content).AppendLine();

        var card = NewCard(title);
        ((StackPanel)card.Child).Children.Add(new TextBox
        {
            Text = content,
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            TextWrapping = TextWrapping.NoWrap,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        SectionsPanel.Children.Add(card);
    }

    private Border NewCard(string title)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 8),
        });

        return new Border
        {
            Style = (Style)FindResource("PanelSection"),
            Child = panel,
        };
    }

    // MARK: - Lecture tolérante du JSON

    private static JsonElement? Find(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out var value) ? value : null;

    private static string? Text(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "Oui",
            JsonValueKind.False => "Non",
            _ => null,
        };
    }

    private static long? Number(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    /// <summary>Ajoute la première clé trouvée parmi plusieurs noms possibles.</summary>
    private static void Add(List<(string, string)> rows, JsonElement source,
                            string label, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (Text(source, key) is { Length: > 0 } value)
            {
                rows.Add((label, value));
                return;
            }
        }
    }

    /// <summary>Convertit une date WMI « 20260812143000.000000+120 » en date lisible.</summary>
    private static string FormatWmiDate(string raw)
    {
        if (raw.Length < 14 || !long.TryParse(raw[..14], out _)) return raw;
        try
        {
            var date = new DateTime(
                int.Parse(raw[..4]), int.Parse(raw[4..6]), int.Parse(raw[6..8]),
                int.Parse(raw[8..10]), int.Parse(raw[10..12]), int.Parse(raw[12..14]));
            return date.ToString("dd/MM/yyyy à HH:mm");
        }
        catch { return raw; }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = ["o", "Ko", "Mo", "Go", "To"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value.ToString(value < 10 ? "0.0" : "0")} {units[unit]}";
    }

    // MARK: - Actions

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_plainText); SetStatus("Informations copiées."); }
        catch { /* presse-papiers verrouillé par une autre application */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
    }
}
