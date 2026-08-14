using Microsoft.Win32;

namespace MeshControl.Core;

/// <summary>
/// Gère le démarrage automatique avec Windows via la clé de registre Run de
/// l'utilisateur courant (HKCU) : aucun droit administrateur requis, et le
/// réglage reste propre à la session Windows.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Mesh Control";

    /// <summary>Chemin de l'exécutable, tel qu'inscrit au démarrage.</summary>
    private static string? ExecutablePath
    {
        get
        {
            // Environment.ProcessPath vise le vrai .exe (et non la dll) — correct
            // aussi bien en publication single-file qu'en exécution classique.
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? null : path;
        }
    }

    /// <summary>Vrai si l'application est inscrite au démarrage de Windows.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrEmpty(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Active ou désactive le démarrage automatique. Renvoie false si l'écriture
    /// dans le registre a échoué (politique de sécurité, profil restreint…).
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;

            if (enabled)
            {
                var exe = ExecutablePath;
                if (exe is null) return false;
                // --minimized : au démarrage de session, l'app se lance directement
                // dans la zone de notification sans ouvrir de fenêtre.
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
