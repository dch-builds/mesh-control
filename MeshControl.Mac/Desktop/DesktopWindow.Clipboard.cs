using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MeshControl.Core;
using MeshControl.Desktop;

namespace MeshControl.Mac;

/// <summary>
/// Presse-papiers : collage, saisie simulée, récupération, et synchronisation
/// automatique dans les deux sens (portage de Desktop/DesktopWindow.xaml.cs et
/// Desktop/DesktopWindow.ClipboardSync.cs).
///
/// Différence avec Windows : macOS ne signale pas les changements du
/// presse-papiers (pas d'équivalent de WM_CLIPBOARDUPDATE). Le contenu local
/// est donc relu au retour dans la fenêtre et à chaque pas de la
/// synchronisation — ce qui suffit au geste « copier ailleurs, revenir, coller ».
/// </summary>
public partial class DesktopWindow
{
    // MARK: - Collage

    /// <summary>
    /// État du presse-papiers de la machine distante : null tant qu'on n'a pas
    /// essayé de l'écrire, vrai s'il accepte l'écriture, faux s'il faut se
    /// replier sur la saisie clavier. Déterminé une fois par session.
    /// </summary>
    private bool? _remoteClipboardWritable;

    /// <summary>
    /// Longueur au-delà de laquelle on n'essaie pas d'écrire le presse-papiers
    /// distant : le texte transite en JSON par le canal de contrôle du serveur.
    /// </summary>
    private const int MaxRemoteClipboardLength = 256 * 1024;

    private CancellationTokenSource? _typingCts;

    /// <summary>Vrai pendant une saisie : le bouton « Taper » devient « Arrêter ».</summary>
    private bool Typing => _typingCts is not null;

    /// <summary>« Copier depuis » en cours : la synchronisation attend son tour.</summary>
    private bool _copyBusy;

    /// <summary>
    /// Colle le presse-papiers local sur la machine distante : écrire le
    /// presse-papiers distant puis envoyer Ctrl+V, instantané quelle que soit
    /// la longueur ; repli sur la saisie caractère par caractère si le serveur
    /// refuse l'écriture ou si l'agent ne la confirme pas.
    /// </summary>
    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        ScreenHost.Focus();
        // Une saisie est en cours : on ne l'interrompt pas par surprise.
        if (Typing)
        {
            ShowStatus("Saisie en cours — « Arrêter » pour l'interrompre.");
            return;
        }
        if (_pasteBusy || RejectedByViewOnly()) return;

        if (await ReadClipboardTextAsync() is not { } text) return;

        // Synchronisation active et texte déjà présent côté distant : Ctrl+V suffit.
        if (_clipboardSync && _remoteClipboardWritable == true
            && _remoteClipKnown is not null && SameClipboardText(text, _remoteClipKnown))
        {
            _session.SendChord([Vk.Ctrl], Vk.Letter('V'));
            ShowStatus($"Collé ({text.Length} car.)");
            return;
        }

        if (_remoteClipboardWritable != false && text.Length <= MaxRemoteClipboardLength)
        {
            _pasteBusy = true;
            UpdateInputControls();
            bool pasted;
            try
            {
                await WaitForClipboardSyncIdleAsync();
                pasted = await TryPasteViaRemoteClipboardAsync(text);
            }
            finally
            {
                _pasteBusy = false;
                UpdateInputControls();
            }
            if (pasted) return;
        }

        await PasteByTypingAsync(text);
    }

    /// <summary>Laisse finir un échange de synchronisation en cours.</summary>
    private async Task WaitForClipboardSyncIdleAsync()
    {
        for (int i = 0; i < 80 && _clipboardSyncBusy; i++)
            await Task.Delay(50);
    }

    /// <summary>
    /// Colle en simulant la frappe. Pendant une saisie, le même bouton
    /// l'interrompt : un long texte immobilise sinon la machine distante.
    /// </summary>
    private async void PasteAsTyping_Click(object? sender, RoutedEventArgs e)
    {
        ScreenHost.Focus();
        if (Typing)
        {
            _typingCts?.Cancel();
            return;
        }
        if (RejectedByViewOnly()) return;
        if (await ReadClipboardTextAsync() is { } text) await PasteByTypingAsync(text);
    }

    /// <summary>
    /// Écrit le texte dans le presse-papiers distant, vérifie (une fois par
    /// session) qu'il y est bien arrivé, puis envoie Ctrl+V. Faux s'il faut se
    /// replier sur la frappe : une erreur silencieuse est bien plus gênante
    /// qu'un collage lent.
    /// </summary>
    private async Task<bool> TryPasteViaRemoteClipboardAsync(string text)
    {
        try
        {
            StatsText.Text = "Envoi vers le presse-papiers distant…";
            await _connection.SetRemoteClipboardAsync(_node.Id, text);
            _remoteClipKnown = text;
            _remoteClipRebaseline = true;

            if (_remoteClipboardWritable is null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var remote = await _connection.GetRemoteClipboardAsync(_node.Id, timeout.Token);
                _remoteClipKnown = remote;
                _remoteClipRebaseline = false;
                _remoteClipboardWritable = SameBeginning(remote, text);

                if (_remoteClipboardWritable == false)
                {
                    Log.Info($"{_node.Name} : le presse-papiers distant n'a pas pris le "
                           + "texte, repli sur la saisie clavier.", "Bureau");
                    return false;
                }
            }

            _session.SendChord([Vk.Ctrl], Vk.Letter('V'));
            ShowStatus($"Collé ({text.Length} car.)");
            return true;
        }
        catch (Exception ex)
        {
            _remoteClipboardWritable = false;
            Log.Info($"{_node.Name} : presse-papiers distant inutilisable "
                   + $"({ex.Message}), repli sur la saisie clavier.", "Bureau");
            return false;
        }
    }

    /// <summary>
    /// Le presse-papiers distant contient-il bien ce qu'on vient d'y écrire ?
    /// Début seulement : l'agent peut tronquer un texte long et normalise les fins de ligne.
    /// </summary>
    private static bool SameBeginning(string remote, string sent)
    {
        const int compared = 200;
        var a = remote.Replace("\r", "");
        var b = (sent.Length > compared * 2 ? sent[..(compared * 2)] : sent).Replace("\r", "");
        if (a.Length == 0 || b.Length == 0) return false;
        var length = Math.Min(Math.Min(a.Length, b.Length), compared);
        return string.CompareOrdinal(a, 0, b, 0, length) == 0;
    }

    /// <summary>Texte du presse-papiers local, ou null (avec message) s'il n'y a rien d'exploitable.</summary>
    private async Task<string?> ReadClipboardTextAsync()
    {
        try
        {
            var text = Host.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
            if (string.IsNullOrEmpty(text))
            {
                ShowStatus("Le presse-papiers ne contient pas de texte.");
                return null;
            }
            return text;
        }
        catch (Exception ex)
        {
            ShowStatus($"Presse-papiers inaccessible : {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Saisit le texte touche par touche. Au-delà de quelques milliers de
    /// caractères, on demande confirmation plutôt que de bloquer la machine.
    /// </summary>
    private async Task PasteByTypingAsync(string text)
    {
        const int warnThreshold = 2000;
        if (text.Length > warnThreshold)
        {
            var seconds = text.Length * 6 / 1000;
            if (!await Dialogs.ConfirmAsync(Host, "Coller un texte long",
                    $"Le presse-papiers contient {text.Length} caractères.\n"
                    + $"La saisie prendra environ {seconds} secondes, pendant lesquelles "
                    + "il ne faut pas utiliser la machine distante.", "Continuer", danger: false))
                return;
        }
        await TypeAsync(text);
    }

    private async Task TypeAsync(string text)
    {
        // Une frappe déjà en cours est annulée : deux saisies entrelaceraient les caractères.
        _typingCts?.Cancel();
        var cts = new CancellationTokenSource();
        _typingCts = cts;
        SetTypingState();
        try
        {
            StatsText.Text = $"Saisie de {text.Length} caractères…";
            await _session.SendTextAsync(text, cts.Token);
            ShowStatus(cts.IsCancellationRequested ? "Saisie interrompue." : "Texte saisi.");
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Saisie interrompue.");
        }
        finally
        {
            // Seule la dernière saisie lancée rétablit les boutons.
            if (ReferenceEquals(_typingCts, cts))
            {
                _typingCts = null;
                SetTypingState();
            }
            cts.Dispose();
        }
    }

    /// <summary>Bloc de collage : Coller grisé, Taper devient Arrêter pendant une saisie.</summary>
    private void SetTypingState()
    {
        var typing = Typing;
        UpdateInputControls();
        PasteTypingGlyph.Kind = BarTypingGlyph.Kind = typing ? IconKind.Stop : IconKind.Keyboard;
        PasteTypingLabel.Text = BarTypingLabel.Text = typing ? "Arrêter" : "Taper";
        ToolTip.SetTip(BarTypingButton, typing ? "Interrompre la saisie en cours" : "Coller en simulant la frappe");
        ToolTip.SetTip(PasteTypingButton, typing
            ? "Interrompre la saisie en cours"
            : "Coller en simulant la frappe : lent, mais fonctionne partout, même là où Ctrl+V est ignoré");
    }

    private async void CopyFromRemote_Click(object? sender, RoutedEventArgs e)
    {
        ScreenHost.Focus();
        if (_copyBusy) return;
        _copyBusy = true;
        try
        {
            await WaitForClipboardSyncIdleAsync();
            var text = await _connection.GetRemoteClipboardAsync(_node.Id);
            if (string.IsNullOrEmpty(text))
            {
                ShowStatus("Presse-papiers distant vide");
                return;
            }
            _remoteClipKnown = text;
            if (Host.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            _lastLocalClip = text;
            ShowStatus("Presse-papiers récupéré");
        }
        catch (Exception ex)
        {
            ShowStatus($"Copie impossible : {ex.Message}");
        }
        finally
        {
            _copyBusy = false;
        }
    }

    // MARK: - Synchronisation automatique

    /// <summary>Synchronisation active pour cette session.</summary>
    private bool _clipboardSync;

    /// <summary>
    /// Ce que l'on croit présent dans le presse-papiers distant : dernier texte
    /// lu ou écrit. Null tant qu'il est inconnu — la première lecture sert alors
    /// de référence et n'est pas recopiée en local.
    /// </summary>
    private string? _remoteClipKnown;

    /// <summary>
    /// Vrai juste après une écriture : la lecture suivante vérifie qu'elle a
    /// pris, puis sert de nouvelle référence au lieu d'être recopiée en local.
    /// </summary>
    private bool _remoteClipRebaseline;

    /// <summary>Le presse-papiers local a changé et n'a pas encore été envoyé.</summary>
    private bool _localPushPending;

    /// <summary>Dernier texte local vu : sert à repérer un changement, faute d'avis du système.</summary>
    private string? _lastLocalClip;

    /// <summary>Un échange de synchronisation est en cours.</summary>
    private bool _clipboardSyncBusy;

    private readonly DispatcherTimer _clipboardPollTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private void InitializeClipboardSync()
    {
        _clipboardPollTimer.Tick += async (_, _) => await ClipboardSyncTickAsync();

        // Retour dans la session après une copie faite ailleurs : envoyer tout
        // de suite, avant que l'utilisateur ne tape Ctrl+V côté distant.

        // Après une reconnexion, le presse-papiers distant a pu être vidé : on
        // repart d'une référence neuve et on renvoie le contenu local.
        _session.StateChanged += () =>
        {
            if (!_clipboardSync || _session.State != DesktopSessionState.Connected) return;
            _remoteClipKnown = null;
            _remoteClipRebaseline = false;
            _localPushPending = true;
        };

        Closed += (_, _) => _clipboardPollTimer.Stop();

        if (App.Settings.DesktopClipboardSync) SetClipboardSync(true, announce: false);
        else UpdateClipboardSyncVisual();
    }

    /// <summary>
    /// Retour dans la session après une copie faite ailleurs : envoyer tout de
    /// suite, avant que l'utilisateur ne tape Ctrl+V côté distant.
    /// </summary>
    protected override async void OnHostActivated()
    {
        if (!_clipboardSync) return;
        await DetectLocalChangeAsync();
        if (_localPushPending) await ClipboardSyncTickAsync();
    }

    private void ClipboardSync_Click(object? sender, RoutedEventArgs e)
    {
        SetClipboardSync(!_clipboardSync, announce: true);
        // Retenu pour les sessions suivantes : c'est une préférence de travail.
        App.Settings.DesktopClipboardSync = _clipboardSync;
        App.Settings.Save();
        ScreenHost.Focus();
    }

    private void SetClipboardSync(bool on, bool announce)
    {
        if (on != _clipboardSync)
        {
            _clipboardSync = on;
            if (on)
            {
                _remoteClipKnown = null;
                _remoteClipRebaseline = false;
                _lastLocalClip = null;
                // À l'activation, le poste local fait foi : son contenu part vers
                // la machine, c'est ce qu'on s'attend à pouvoir y coller.
                _localPushPending = true;
                _clipboardPollTimer.Start();
            }
            else
            {
                _clipboardPollTimer.Stop();
                _localPushPending = false;
            }
            Log.Info($"{_node.Name} : synchronisation du presse-papiers " + (on ? "activée" : "désactivée") + ".", "Bureau");
        }

        if (announce) ShowStatus(on ? "Presse-papiers synchronisé." : "Synchronisation du presse-papiers arrêtée.");
        UpdateClipboardSyncVisual();
    }

    private void UpdateClipboardSyncVisual()
    {
        SetToggleLook(ClipboardSyncButton, BarSyncButton, _clipboardSync, toolbarOffBackground: Brushes.Transparent);
        var tip = _clipboardSync
            ? "Presse-papiers synchronisé avec la machine (texte, dans les deux sens). Cliquer pour arrêter."
            : "Synchroniser automatiquement le presse-papiers (texte) avec la machine, dans les deux sens";
        ToolTip.SetTip(ClipboardSyncButton, tip);
        ToolTip.SetTip(BarSyncButton, tip);
    }

    /// <summary>
    /// La synchronisation peut-elle échanger maintenant ? Session connectée, au
    /// premier plan : la réponse de l'agent ne porte pas l'identifiant de la
    /// machine, deux sessions interrogeant en même temps pourraient échanger
    /// leurs réponses ; une seule fenêtre est active à la fois.
    /// </summary>
    private bool ClipboardSyncCanExchange
        => _clipboardSync && _session.State == DesktopSessionState.Connected
           && Host.IsActive && Host.WindowState != WindowState.Minimized
           // En onglet : seulement la session affichée.
           && TopLevel.GetTopLevel(RootPanel) is not null;

    /// <summary>Repère un changement du presse-papiers local depuis la dernière lecture.</summary>
    private async Task DetectLocalChangeAsync()
    {
        var text = await TryReadLocalClipboardTextAsync();
        if (string.IsNullOrEmpty(text) || text == _lastLocalClip) return;
        _lastLocalClip = text;
        // Écho de ce que l'on vient soi-même de recevoir de la machine.
        if (_remoteClipKnown is not null && SameClipboardText(text, _remoteClipKnown)) return;
        _localPushPending = true;
    }

    /// <summary>
    /// Un pas de synchronisation : envoi du contenu local s'il a changé, sinon
    /// relecture du presse-papiers distant.
    /// </summary>
    private async Task ClipboardSyncTickAsync()
    {
        if (_clipboardSyncBusy || !ClipboardSyncCanExchange) return;
        // Collage ou « Copier depuis » en cours : même attente de réponse côté connexion.
        if (_pasteBusy || _copyBusy) return;

        _clipboardSyncBusy = true;
        try
        {
            await DetectLocalChangeAsync();
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

    private async Task PushLocalClipboardAsync()
    {
        var text = await TryReadLocalClipboardTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        if (_remoteClipKnown is not null && SameClipboardText(text, _remoteClipKnown)) return;

        if (text.Length > MaxRemoteClipboardLength)
        {
            ShowStatus("Presse-papiers trop volumineux pour être synchronisé.");
            return;
        }

        try
        {
            await _connection.SetRemoteClipboardAsync(_node.Id, text);
            _remoteClipKnown = text;
            _remoteClipRebaseline = true;
            ShowStatus($"Presse-papiers → machine ({text.Length} car.)");
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
            // Délai court : un presse-papiers distant vide (ou une image) ne
            // reçoit aucune réponse de l'agent, ce n'est pas une erreur.
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
            if (SameBeginning(remote, _remoteClipKnown)) _remoteClipboardWritable = true;
            else
                Log.Info($"{_node.Name} : le presse-papiers distant n'a pas pris le texte "
                       + "synchronisé (clipboardset désactivé sur le serveur ?).", "Bureau");
            _remoteClipKnown = remote;
            return;
        }

        if (string.IsNullOrEmpty(remote) || SameClipboardText(remote, _remoteClipKnown)) return;

        // Garde-fous, le texte arrivant sans action de l'utilisateur : taille
        // bornée, et pas de caractères de contrôle — une machine hostile
        // pourrait sinon y glisser des séquences qui, collées dans un terminal,
        // piloteraient le shell d'une autre.
        if (remote.Length > MaxRemoteClipboardLength)
        {
            _remoteClipKnown = remote;
            ShowStatus($"Presse-papiers de la machine trop volumineux ({remote.Length / 1024} Ko) : non recopié.");
            return;
        }
        var local = WithoutControlCharacters(remote);

        // La référence est mise à jour d'abord : la relecture locale suivante
        // sera reconnue comme un écho.
        var previous = _remoteClipKnown;
        _remoteClipKnown = remote;
        try
        {
            if (Host.Clipboard is { } clipboard) await clipboard.SetTextAsync(local);
            _lastLocalClip = local;
            ShowStatus($"Presse-papiers ← machine ({local.Length} car.)");
        }
        catch
        {
            // Presse-papiers local occupé : on retentera au prochain pas.
            _remoteClipKnown = previous;
        }
    }

    /// <summary>Texte du presse-papiers local, sans message d'erreur (usage en tâche de fond).</summary>
    private async Task<string?> TryReadLocalClipboardTextAsync()
    {
        try { return Host.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null; }
        catch { return null; }
    }

    /// <summary>Texte sans caractères de contrôle (tabulations et fins de ligne gardées).</summary>
    private static string WithoutControlCharacters(string text)
    {
        static bool Unwanted(char c) =>
            (c < 0x20 && c is not '\t' and not '\r' and not '\n') || (c >= 0x7F && c <= 0x9F);
        return text.Any(Unwanted) ? new string(text.Where(c => !Unwanted(c)).ToArray()) : text;
    }

    /// <summary>Égalité aux fins de ligne près : l'agent les normalise au passage.</summary>
    private static bool SameClipboardText(string a, string b) =>
        string.Equals(a.Replace("\r", ""), b.Replace("\r", ""), StringComparison.Ordinal);
}
