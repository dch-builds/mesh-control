using System.Drawing;
using System.Drawing.Imaging;

// Alias locaux : les types WinForms sont qualifiés explicitement ici, car les
// alias globaux du projet (GlobalUsings.cs) donnent la priorité à WPF.
using WinForms = System.Windows.Forms;
using DrawingFontStyle = System.Drawing.FontStyle;

namespace MeshControl.UI;

/// <summary>
/// Icône dans la zone de notification Windows. Encapsule NotifyIcon (WinForms),
/// seule voie disponible pour le tray — WPF n'en propose pas d'équivalent.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;

    public event Action? OpenRequested;
    public event Action? QuickSearchRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _icon = new WinForms.NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "Mesh Control",
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();

        var openItem = new WinForms.ToolStripMenuItem("Ouvrir Mesh Control");
        openItem.Font = new Font(openItem.Font, DrawingFontStyle.Bold);
        openItem.Click += (_, _) => OpenRequested?.Invoke();
        menu.Items.Add(openItem);

        var searchItem = new WinForms.ToolStripMenuItem("Recherche rapide…");
        searchItem.Click += (_, _) => QuickSearchRequested?.Invoke();
        menu.Items.Add(searchItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exitItem = new WinForms.ToolStripMenuItem("Quitter");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        _icon.ContextMenuStrip = menu;
        // Double-clic sur l'icône : raccourci vers l'ouverture de la fenêtre.
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>Met à jour l'infobulle (état de connexion, nombre de machines…).</summary>
    public void SetTooltip(string text)
    {
        // NotifyIcon.Text est limité à 63 caractères par Windows.
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    /// <summary>Notification discrète (utilisée à la première réduction).</summary>
    public void ShowMessage(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(3000);
    }

    /// <summary>
    /// Construit l'icône par code (un « W » sur fond bleu Mesh Control), ce qui évite
    /// d'embarquer un fichier .ico dans le projet.
    /// </summary>
    private static Icon BuildIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var background = new SolidBrush(ColorTranslator.FromHtml("#0A84FF"));
            g.FillEllipse(background, 0, 0, size - 1, size - 1);

            using var font = new Font("Segoe UI", 15, DrawingFontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(System.Drawing.Color.White);
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("W", font, text, new RectangleF(0, 0, size, size), format);
        }

        // Icon.FromHandle emprunte le handle : on clone pour posséder l'icône et
        // pouvoir libérer le bitmap sans l'invalider.
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
