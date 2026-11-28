using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Icône de la barre des menus — l'équivalent de la zone de notification de
/// la version Windows (UI/TrayIcon.cs) : la fermeture de la fenêtre garde
/// l'application active, joignable par le raccourci global et par l'icône.
/// Seul « Quitter » (ou ⌘Q) l'arrête.
/// </summary>
public partial class MainWindow
{
    private TrayIcon? _tray;
    private NativeMenuItem? _traySearchItem;

    /// <summary>Le libellé du menu suit le raccourci choisi dans les réglages.</summary>
    private void UpdateTrayMenu()
    {
        if (_traySearchItem is not null)
            _traySearchItem.Header = _hotkey?.IsRegistered == true ? $"Recherche rapide…  {HotkeyLabel}" : "Recherche rapide…";
    }

    /// <summary>Vrai quand l'utilisateur quitte vraiment l'application.</summary>
    private bool _exiting;

    /// <summary>Le rappel « l'application reste active » n'est montré qu'une fois.</summary>
    private bool _trayHintShown;

    private void SetupTray()
    {
        if (_tray is not null || Application.Current is not { } app) return;

        var open = new NativeMenuItem("Ouvrir Mesh Control");
        open.Click += (_, _) => RestoreMainWindow();
        var search = _traySearchItem = new NativeMenuItem($"Recherche rapide…  {HotkeyLabel}");
        search.Click += (_, _) => ShowQuickSearch();
        var quit = new NativeMenuItem("Quitter");
        quit.Click += (_, _) => Quit();

        _tray = new TrayIcon
        {
            ToolTipText = "Mesh Control — non connecté",
            Menu = new NativeMenu { Items = { open, search, new NativeMenuItemSeparator(), quit } },
            IsVisible = true,
        };
        try { _tray.Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MeshControl.Mac/Assets/app.png"))); }
        catch { /* icône absente : celle par défaut */ }
        // Sous Windows, un clic sur l'icône ouvre la fenêtre ; sous macOS, il déroule le menu.
        _tray.Clicked += (_, _) => RestoreMainWindow();
        TrayIcon.SetIcons(app, new TrayIcons { _tray });

        // Clic sur l'icône du Dock, fenêtre masquée : elle revient.
        if (app.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) RestoreMainWindow();
            };
        }

        // ⌘Q, menu de l'application, fermeture de session : on quitte vraiment.
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.ShutdownRequested += (_, _) => _exiting = true;

        Closing += (_, e) =>
        {
            if (_exiting || !Settings.KeepInMenuBar) return;
            e.Cancel = true;
            HideToMenuBar();
        };
        Closed += (_, _) =>
        {
            _tray?.Dispose();
            if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime) lifetime.Shutdown();
        };
    }

    private void HideToMenuBar()
    {
        Hide();
        if (_trayHintShown) return;
        _trayHintShown = true;
        SystemNotification.Show("Mesh Control reste actif",
            $"L'application continue de tourner dans la barre des menus. Raccourci : {HotkeyLabel}");
    }

    private void Quit()
    {
        _exiting = true;
        Close();
    }

    /// <summary>Info-bulle de l'icône : état de la connexion et machines en ligne.</summary>
    private void UpdateTrayTooltip()
    {
        if (_tray is null) return;
        _tray.ToolTipText = _connection?.State == ConnectionState.Connected
            ? $"Mesh Control — {_allRows.Count(r => r.IsOnline)} machine(s) en ligne"
            : "Mesh Control — non connecté";
    }

    /// <summary>
    /// Démarrage réduit (réglage, ou « --minimized » transmis par l'ouverture à
    /// la connexion de session) : connexion en tâche de fond, sans montrer la
    /// fenêtre. Faux s'il faut l'afficher.
    /// </summary>
    public static bool ShouldStartHidden()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase))) return false;
        return !string.IsNullOrWhiteSpace(App.Settings.Host)
               && (App.Settings.StartMinimized || args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Lance l'application sans afficher la fenêtre (voir <see cref="ShouldStartHidden"/>).</summary>
    public void StartHidden() => Initialize();
}
