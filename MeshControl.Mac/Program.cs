using Avalonia;

namespace MeshControl.Mac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Avant toute initialisation : Velopack peut avoir à terminer une mise à
        // jour (premier lancement, retour arrière) et quitter aussitôt.
        Velopack.VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Utilisé aussi par le concepteur visuel d'Avalonia.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .LogToTrace();
}
