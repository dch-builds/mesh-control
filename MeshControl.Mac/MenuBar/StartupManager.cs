using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Ouverture de l'application à la connexion de session (portage de
/// Core/StartupManager.cs, qui passe par la clé Run du registre Windows).
///
/// Sous macOS : un agent de lancement de l'utilisateur
/// (~/Library/LaunchAgents), qui démarre l'application réduite dans la barre
/// des menus. Ne demande aucune autorisation ; l'utilisateur le retrouve et
/// peut le couper dans Réglages Système › Général › Ouverture.
/// </summary>
public static class StartupManager
{
    private const string Label = "com.example.meshcontrol.launcher";

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    /// <summary>Le démarrage automatique n'a de sens que sous macOS (l'app Windows a le sien).</summary>
    public static bool IsSupported => OperatingSystem.IsMacOS();

    public static bool IsEnabled() => IsSupported && File.Exists(PlistPath);

    /// <summary>Active ou retire le démarrage automatique. Faux en cas d'échec.</summary>
    public static bool SetEnabled(bool enabled)
    {
        if (!IsSupported) return false;
        try
        {
            if (!enabled)
            {
                if (File.Exists(PlistPath)) File.Delete(PlistPath);
                return true;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable)) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            File.WriteAllText(PlistPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key>
                  <string>{Label}</string>
                  <key>ProgramArguments</key>
                  <array>
                    <string>{System.Security.SecurityElement.Escape(executable)}</string>
                    <string>--minimized</string>
                  </array>
                  <key>RunAtLoad</key>
                  <true/>
                  <key>ProcessType</key>
                  <string>Interactive</string>
                </dict>
                </plist>
                """);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"Démarrage automatique : {ex.Message}", "Réglages");
            return false;
        }
    }
}
