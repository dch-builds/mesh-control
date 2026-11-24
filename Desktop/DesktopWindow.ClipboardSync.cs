using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeshControl.Core;

namespace MeshControl.Desktop;

/// <summary>
/// Synchronisation automatique du presse-papiers (texte) avec la machine
/// distante, dans les deux sens.
///
/// - Local → distant : Windows signale chaque changement du presse-papiers
///   local (WM_CLIPBOARDUPDATE). Le texte part vers la machine dès que la
///   session est au premier plan ; sinon il attend qu'on y revienne. Copier
///   dans le Bloc-notes puis cliquer dans la session suffit donc pour coller
///   avec un simple Ctrl+V côté distant.
/// - Distant → local : l'agent MeshCentral ne signale pas les changements de
///   son presse-papiers. On le relit toutes les deux secondes, uniquement
///   pour la session au premier plan.
///
/// Pourquoi seulement au premier plan : la réponse de l'agent ne porte pas
/// l'identifiant de la machine, et la connexion n'attend qu'une réponse à la
/// fois (voir MeshServerConnection). Deux sessions interrogeant en même temps
/// pourraient échanger leurs réponses ; une seule fenêtre est active à la fois.
/// </summary>
public partial class DesktopWindow
{
    /// <summary>Synchronisation active pour cette session.</summary>
    private bool _clipboardSync;

    /// <summary>Fenêtre invisible qui reçoit les avis de changement du presse-papiers local.</summary>
    private HwndSource? _clipboardListener;

    /// <summary>
    /// Ce que l'on croit présent dans le presse-papiers distant : dernier texte
    /// lu ou écrit. Null tant qu'il est inconnu — la première lecture sert alors
    /// de référence et n'est pas recopiée en local, pour ne pas écraser le
    /// presse-papiers du poste à l'ouverture d'une session.
    /// </summary>
    private string? _remoteClipKnown;

    /// <summary>
    /// Vrai juste après une écriture : la lecture suivante vérifie qu'elle a
    /// pris, puis sert de nouvelle référence au lieu d'être recopiée en local.
    /// Sans cela, une écriture refusée par le serveur ramènerait l'ancien
    /// contenu distant dans le presse-papiers local.
    /// </summary>
    private bool _remoteClipRebaseline;

    /// <summary>Le presse-papiers local a changé et n'a pas encore été envoyé.</summary>
    private bool _localPushPending;

    /// <summary>Un échange de synchronisation est en cours.</summary>
    private bool _clipboardSyncBusy;

    private readonly DispatcherTimer _clipboardPollTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2),
    };

    /// <summary>
    /// Regroupe les avis rapprochés : une copie produit souvent plusieurs
    /// formats, donc plusieurs WM_CLIPBOARDUPDATE à la suite.
    /// </summary>
    private readonly DispatcherTimer _localClipboardDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(250),
    };

    private void InitializeClipboardSync()
    {
        _clipboardPollTimer.Tick += async (_, _) => await ClipboardSyncTickAsync();
        _localClipboardDebounce.Tick += async (_, _) =>
        {
            _localClipboardDebounce.Stop();
            await OnLocalClipboardChangedAsync();
        };

        // Retour dans la session après une copie faite ailleurs : envoyer tout
        // de suite, avant que l'utilisateur ne tape Ctrl+V côté distant.
        RootPanel.IsKeyboardFocusWithinChanged += async (_, e) =>
        {
            if (e.NewValue is true && _localPushPending) await ClipboardSyncTickAsync();
        };

        // Après une reconnexion (redémarrage de la machine notamment), le
        // presse-papiers distant a pu être vidé : on repart d'une référence
        // neuve et on renvoie le contenu local.
        _session.StateChanged += () =>
        {
            if (!_clipboardSync || _session.State != DesktopSessionState.Connected) return;
            _remoteClipKnown = null;
            _remoteClipRebaseline = false;
            _localPushPending = true;
        };

        Closed += (_, _) =>
        {
            _clipboardPollTimer.Stop();
            _localClipboardDebounce.Stop();
            StopClipboardListener();
        };

        if (_settings.DesktopClipboardSync) SetClipboardSync(true, announce: false);
        else UpdateClipboardSyncVisual();
    }

    private void ClipboardSync_Click(object sender, RoutedEventArgs e)
    {
        SetClipboardSync(!_clipboardSync, announce: true);

        // Retenu pour les sessions suivantes : c'est une préférence de travail,
        // pas un réglage propre à une machine.
        _settings.DesktopClipboardSync = _clipboardSync;
        _settings.Save();
        RootPanel.Focus();
    }

    private void SetClipboardSync(bool on, bool announce)
    {
        if (on != _clipboardSync)
        {
            _clipboardSync = on;

            if (on)
            {
                StartClipboardListener();
                _remoteClipKnown = null;
                _remoteClipRebaseline = false;
                // À l'activation, le poste local fait foi : son contenu part
                // vers la machine, c'est ce qu'on s'attend à pouvoir y coller.
                _localPushPending = true;
                _clipboardPollTimer.Start();
            }
            else
            {
                _clipboardPollTimer.Stop();
                _localClipboardDebounce.Stop();
                StopClipboardListener();
                _localPushPending = false;
            }

            Log.Info($"{_node.Name} : synchronisation du presse-papiers "
                   + (on ? "activée" : "désactivée") + ".", "Bureau");
        }

        if (announce)
        {
            StatsText.Text = on
                ? "Presse-papiers synchronisé."
                : "Synchronisation du presse-papiers arrêtée.";
        }
        UpdateClipboardSyncVisual();
    }

    private void UpdateClipboardSyncVisual()
    {
        SetToggleLook(ClipboardSyncButton, BarSyncButton, _clipboardSync,
                      toolbarOffBackground: Brushes.Transparent);

        var tip = _clipboardSync
            ? "Presse-papiers synchronisé avec la machine (texte, dans les deux sens). "
              + "Cliquer pour arrêter."
            : "Synchroniser automatiquement le presse-papiers (texte) avec la machine, "
              + "dans les deux sens";
        ClipboardSyncButton.ToolTip = tip;
        BarSyncButton.ToolTip = tip;
    }

    /// <summary>
    /// La synchronisation peut-elle échanger maintenant ? Session connectée,
    /// affichée et au premier plan — voir la remarque en tête de fichier.
    /// </summary>
    private bool ClipboardSyncCanExchange
    {
        get
        {
            if (!_clipboardSync || _session.State != DesktopSessionState.Connected) return false;
            if (!RootPanel.IsVisible) return false;
            var host = Window.GetWindow(RootPanel);
            return host is { IsActive: true } && host.WindowState != WindowState.Minimized;
        }
    }

    /// <summary>
    /// Un pas de synchronisation : envoi du contenu local s'il a changé, sinon
    /// relecture du presse-papiers distant.
    /// </summary>
    private async Task ClipboardSyncTickAsync()
    {
        if (_clipboardSyncBusy || !ClipboardSyncCanExchange) return;

        // Collage ou « Copier depuis » en cours : ils utilisent la même attente
        // de réponse côté connexion, il ne faut pas leur couper l'herbe sous le pied.
        if (_pasteBusy || !CopyFromRemoteButton.IsEnabled) return;

        _clipboardSyncBusy = true;
        try
        {
            if (_localPushPending)
            {
                _localPushPending = false;
                await PushLocalClipboardAsync();
            }
            else
            {
                await PollRemoteClipboardAsync();
            }
        }
        finally
        {
            _clipboardSyncBusy = false;
        }
    }

    private async Task OnLocalClipboardChangedAsync()
    {
        if (!_clipboardSync) return;

        var text = TryReadLocalClipboardText();
        if (string.IsNullOrEmpty(text)) return;

        // Écho de ce que l'on vient soi-même de recevoir de la machine.
        if (_remoteClipKnown is not null && SameClipboardText(text, _remoteClipKnown)) return;

        _localPushPending = true;
        await ClipboardSyncTickAsync();   // sans effet si la session n'est pas au premier plan
    }

    private async Task PushLocalClipboardAsync()
    {
        var text = TryReadLocalClipboardText();
        if (string.IsNullOrEmpty(text)) return;
        if (_remoteClipKnown is not null && SameClipboardText(text, _remoteClipKnown)) return;

        if (text.Length > MaxRemoteClipboardLength)
        {
            StatsText.Text = "Presse-papiers trop volumineux pour être synchronisé.";
            return;
        }

        try
        {
            await _connection.SetRemoteClipboardAsync(_node.Id, text);
            _remoteClipKnown = text;
            _remoteClipRebaseline = true;
            StatsText.Text = $"Presse-papiers → machine ({text.Length} car.)";
        }
        catch (Exception ex)
        {
            // Réessayé au pas suivant.
            _localPushPending = true;
            Log.Debug($"Synchronisation : envoi du presse-papiers impossible ({ex.Message})", "Bureau");
        }
    }

    private async Task PollRemoteClipboardAsync()
    {
        string remote;
        try
        {
            // Délai court : un presse-papiers distant vide (ou contenant une
            // image) ne reçoit aucune réponse de l'agent, ce n'est pas une erreur.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            remote = await _connection.GetRemoteClipboardAsync(_node.Id, timeout.Token);
        }
        catch
        {
            return;
        }

        if (!_clipboardSync) return;   // désactivée pendant l'attente

        // Première lecture : simple référence.
        if (_remoteClipKnown is null)
        {
            _remoteClipKnown = remote;
            return;
        }

        // Lecture de contrôle après une écriture.
        if (_remoteClipRebaseline)
        {
            _remoteClipRebaseline = false;
            if (SameClipboardBeginning(remote, _remoteClipKnown))
            {
                _remoteClipboardWritable = true;
            }
            else
            {
                Log.Info($"{_node.Name} : le presse-papiers distant n'a pas pris le texte "
                       + "synchronisé (clipboardset désactivé sur le serveur ?).", "Bureau");
            }
            _remoteClipKnown = remote;
            return;
        }

        if (string.IsNullOrEmpty(remote) || SameClipboardText(remote, _remoteClipKnown)) return;

        // Le presse-papiers distant a changé : on le recopie en local. La
        // référence est mise à jour d'abord, pour que l'avis de changement
        // local qui va suivre soit reconnu comme un écho.
        var previous = _remoteClipKnown;
        _remoteClipKnown = remote;
        try
        {
            Clipboard.SetText(remote);
            StatsText.Text = $"Presse-papiers ← machine ({remote.Length} car.)";
        }
        catch
        {
            // Presse-papiers local occupé par une autre application : on
            // retentera au prochain pas.
            _remoteClipKnown = previous;
        }
    }

    /// <summary>Texte du presse-papiers local, sans message d'erreur (usage en tâche de fond).</summary>
    private static string? TryReadLocalClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Égalité aux fins de ligne près : l'agent les normalise au passage.</summary>
    private static bool SameClipboardText(string a, string b) =>
        string.Equals(a.Replace("\r", ""), b.Replace("\r", ""), StringComparison.Ordinal);

    /// <summary>Comparaison du début seulement : l'agent peut tronquer un texte long.</summary>
    private static bool SameClipboardBeginning(string remote, string sent) =>
        SameBeginning(remote, sent);

    // MARK: - Écoute du presse-papiers local

    private const int WM_CLIPBOARDUPDATE = 0x031D;

    /// <summary>Parent des fenêtres « message seulement » : invisibles, sans barre des tâches.</summary>
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    /// <summary>
    /// Fenêtre propre plutôt que celle de la session : en mode onglets, la
    /// fenêtre de la session n'est jamais créée, et la fenêtre hôte change au
    /// gré des déplacements d'onglets.
    /// </summary>
    private void StartClipboardListener()
    {
        if (_clipboardListener is not null) return;

        var parameters = new HwndSourceParameters("MeshControlClipboardListener")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _clipboardListener = new HwndSource(parameters);
        _clipboardListener.AddHook(ClipboardListenerHook);

        if (!AddClipboardFormatListener(_clipboardListener.Handle))
        {
            Log.Warning("Écoute du presse-papiers local impossible "
                      + $"(erreur {Marshal.GetLastWin32Error()}) : seul le sens "
                      + "machine → poste sera synchronisé.", "Bureau");
        }
    }

    private void StopClipboardListener()
    {
        if (_clipboardListener is not { } listener) return;
        _clipboardListener = null;

        RemoveClipboardFormatListener(listener.Handle);
        listener.RemoveHook(ClipboardListenerHook);
        listener.Dispose();
    }

    private IntPtr ClipboardListenerHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                                         ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            _localClipboardDebounce.Stop();
            _localClipboardDebounce.Start();
        }
        return IntPtr.Zero;
    }

    // MARK: - Apparence des interrupteurs

    /// <summary>
    /// Met en évidence un interrupteur actif, dans la barre d'outils et dans
    /// la barre flottante du plein écran.
    /// </summary>
    /// <param name="toolbarOffBackground">
    /// Fond au repos du bouton de barre d'outils, quand il diffère de celui
    /// du style (bouton intégré à un bloc segmenté) ; null pour le style.
    /// </param>
    private void SetToggleLook(Button toolbarButton, Button barButton, bool on,
                               Brush? toolbarOffBackground = null)
    {
        if (on)
        {
            toolbarButton.Background = (Brush)FindResource("AccentSoftBrush");
            toolbarButton.Foreground = (Brush)FindResource("AccentBrush");
            barButton.Background = ActiveOverlayBrush;
        }
        else
        {
            if (toolbarOffBackground is not null) toolbarButton.Background = toolbarOffBackground;
            else toolbarButton.ClearValue(BackgroundProperty);
            toolbarButton.ClearValue(ForegroundProperty);
            barButton.ClearValue(BackgroundProperty);
        }
    }

    /// <summary>Fond d'un interrupteur actif sur la barre sombre du plein écran.</summary>
    private static readonly Brush ActiveOverlayBrush = CreateFrozenBrush(0x59, 0x0A, 0x84, 0xFF);

    private static Brush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
