using Avalonia;

namespace MeshControl.Mac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Utilisé aussi par le concepteur visuel d'Avalonia.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .LogToTrace();
}
