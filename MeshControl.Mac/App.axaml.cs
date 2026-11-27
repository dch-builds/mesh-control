using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MeshControl.Core;

namespace MeshControl.Mac;

public partial class App : Application
{
    public static MacSettings Settings { get; } = MacSettings.Load();

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
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
