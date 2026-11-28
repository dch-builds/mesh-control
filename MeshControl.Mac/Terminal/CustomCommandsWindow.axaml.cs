using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Gestion des commandes personnalisées du menu « Commandes » du terminal
/// (portage de SSH/CustomCommandsWindow.xaml.cs) : ajout, modification,
/// suppression, ordre. Rien n'est enregistré avant « Enregistrer ».
/// </summary>
public partial class CustomCommandsWindow : ChromeWindow
{
    private readonly ObservableCollection<CommandEntry> _entries = new();
    private readonly CustomCommandShell _defaultShell;

    /// <param name="defaultShell">Shell proposé pour une nouvelle commande : celui du terminal.</param>
    /// <param name="newCommand">
    /// Texte d'une commande à créer d'emblée (sélection du terminal), ou chaîne
    /// vide pour une commande vierge ; null pour ouvrir simplement la liste.
    /// </param>
    public CustomCommandsWindow(CustomCommandShell defaultShell, string? newCommand = null)
    {
        InitializeComponent();
        Title = "Mes commandes — Mesh Control";
        _defaultShell = defaultShell;

        foreach (var command in App.Settings.CustomTerminalCommands)
            _entries.Add(new CommandEntry(command.Clone()));
        CommandList.ItemsSource = _entries;

        if (newCommand is not null) AddEntry(newCommand);
        else if (_entries.Count > 0) CommandList.SelectedIndex = 0;
        UpdateEditor();

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(false);
        };
        Opened += (_, _) =>
        {
            if (CommandList.SelectedItem is null) return;
            // Commande reprise de la sélection : il ne manque que le libellé.
            if (newCommand is { Length: > 0 }) LabelBox.Focus();
            else if (newCommand is not null) CommandBox.Focus();
        };
    }

    private CommandEntry? Selected => CommandList.SelectedItem as CommandEntry;

    private void AddEntry(string command)
    {
        var entry = new CommandEntry(new CustomTerminalCommand { Command = command, Shell = _defaultShell });
        _entries.Add(entry);
        CommandList.SelectedItem = entry;
        CommandList.ScrollIntoView(entry);
    }

    private void CommandList_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateEditor();

    private void UpdateEditor()
    {
        Editor.DataContext = Selected;
        Editor.IsEnabled = Selected is not null;
        EmptyText.IsVisible = _entries.Count == 0;

        var index = CommandList.SelectedIndex;
        DeleteButton.IsEnabled = index >= 0;
        UpButton.IsEnabled = index > 0;
        DownButton.IsEnabled = index >= 0 && index < _entries.Count - 1;
    }

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        AddEntry("");
        CommandBox.Focus();
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        var index = CommandList.SelectedIndex;
        _entries.Remove(entry);
        if (_entries.Count > 0) CommandList.SelectedIndex = Math.Min(index, _entries.Count - 1);
        UpdateEditor();
    }

    private void Up_Click(object? sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object? sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        var index = CommandList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _entries.Count) return;
        _entries.Move(index, target);
        CommandList.SelectedIndex = target;
        UpdateEditor();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        App.Settings.CustomTerminalCommands = _entries
            .Select(entry => entry.Command)
            .Where(command => !string.IsNullOrWhiteSpace(command.Command))
            .Select(command =>
            {
                command.Command = command.Command.TrimEnd('\r', '\n');
                command.Label = string.IsNullOrWhiteSpace(command.Label)
                    ? CommandEntry.DefaultLabel(command.Command)
                    : command.Label.Trim();
                return command;
            })
            .ToList();
        App.Settings.Save();
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}

/// <summary>Ligne de la liste des commandes, liée aux champs d'édition.</summary>
public sealed class CommandEntry : INotifyPropertyChanged
{
    public CommandEntry(CustomTerminalCommand command) => Command = command;

    /// <summary>Commande modifiée sur place : c'est elle qui sera enregistrée.</summary>
    public CustomTerminalCommand Command { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label
    {
        get => Command.Label;
        set { Command.Label = value; Notify(); Notify(nameof(DisplayLabel)); }
    }

    public string CommandValue
    {
        get => Command.Command;
        set { Command.Command = value; Notify(); Notify(nameof(DisplayLabel)); }
    }

    public bool Run
    {
        get => Command.Run;
        set { Command.Run = value; Notify(); }
    }

    public int ShellIndex
    {
        get => (int)Command.Shell;
        set
        {
            if (value < 0) return;
            Command.Shell = (CustomCommandShell)value;
            Notify();
            Notify(nameof(ShellLabel));
        }
    }

    /// <summary>Libellé, ou à défaut le début de la commande.</summary>
    public string DisplayLabel => !string.IsNullOrWhiteSpace(Command.Label)
        ? Command.Label
        : string.IsNullOrWhiteSpace(Command.Command) ? "(nouvelle commande)" : DefaultLabel(Command.Command);

    public string ShellLabel => Command.Shell switch
    {
        CustomCommandShell.Cmd => "cmd",
        CustomCommandShell.PowerShell => "PowerShell",
        CustomCommandShell.Unix => "Linux / macOS",
        _ => "Tous les shells",
    };

    /// <summary>Première ligne de la commande, raccourcie.</summary>
    public static string DefaultLabel(string command)
    {
        var line = command.Split('\n')[0].Trim();
        return line.Length > 40 ? line[..40] + "…" : line;
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
