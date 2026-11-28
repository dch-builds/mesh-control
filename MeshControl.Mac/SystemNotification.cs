using System.Diagnostics;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Notification du système, l'équivalent des bulles de la zone de notification
/// de la version Windows. Sous macOS, elle passe par AppleScript (« display
/// notification »), disponible sans signature ni autorisation préalable. Ailleurs
/// (essais sous Windows), seul le bandeau de la fenêtre principale s'affiche.
/// </summary>
public static class SystemNotification
{
    public static void Show(string title, string message)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var script = $"display notification {Quote(message)} with title \"Mesh Control\" subtitle {Quote(title)}";
            using var process = Process.Start(new ProcessStartInfo("osascript")
            {
                ArgumentList = { "-e", script },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Debug($"Notification impossible : {ex.Message}", "Système");
        }
    }

    /// <summary>Chaîne AppleScript : guillemets et barres obliques échappés.</summary>
    private static string Quote(string text)
        => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
