using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MeshControl.Core;

namespace MeshControl.Mac;

public partial class App : Application
{
    public static MacSettings Settings { get; } = MacSettings.Load();

    /// <summary>Fenêtre principale, même quand elle démarre masquée.</summary>
    public static MainWindow? Main { get; private set; }

    public override void Initialize()
    {
        // Palette d'abord : les styles et les fenêtres y puisent leurs couleurs.
        Palette.Apply(this, Settings.DarkMode);
        Palette.ApplyDensity(this, Settings.ListDensity);
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        CertificateTrust.Initialize(Settings);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // L'application vit dans la barre des menus : fermer la fenêtre ne
            // la quitte pas (voir MainWindow.MenuBar.cs).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var main = new MainWindow();
            Main = main;
            if (MainWindow.ShouldStartHidden()) main.StartHidden();
            else desktop.MainWindow = main;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
