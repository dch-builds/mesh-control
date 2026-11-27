using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeshControl.Mac;

/// <summary>
/// Boîtes de dialogue de l'application : confirmation d'une action, et saisie
/// d'un texte (renommage). Même habillage que les autres fenêtres.
/// </summary>
public static class Dialogs
{
    /// <summary>Demande confirmation ; vrai si l'utilisateur accepte.</summary>
    public static Task<bool> ConfirmAsync(Window owner, string title, string detail,
                                          string accept = "OK", bool danger = true)
    {
        var dialog = Create(title, out var body, out var buttons);
        body.Children.Add(new TextBlock
        {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Foreground = Palette.Resource("TextSecondaryBrush"),
        });
        AddButtons(dialog, buttons, accept, danger, () => dialog.Close(true));
        return dialog.ShowDialog<bool>(owner);
    }

    /// <summary>Demande un texte ; null si l'utilisateur annule.</summary>
    public static Task<string?> PromptAsync(Window owner, string title, string label, string initial)
    {
        var dialog = Create(title, out var body, out var buttons);
        body.Children.Add(new TextBlock { Text = label, Classes = { "caption" }, Margin = new Thickness(0, 0, 0, 5) });
        var box = new TextBox { Text = initial };
        body.Children.Add(box);
        void Accept() => dialog.Close((box.Text ?? "").Trim());
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Accept(); } };
        AddButtons(dialog, buttons, "Renommer", danger: false, Accept);
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog<string?>(owner);
    }

    private static ChromeWindow Create(string title, out StackPanel body, out StackPanel buttons)
    {
        var dialog = new ChromeWindow
        {
            Title = "Mesh Control",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        body = new StackPanel { Margin = new Thickness(24, 20, 24, 0) };
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.Resource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 8),
        });

        buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(24, 20, 24, 20),
            Spacing = 8,
        };

        var root = new DockPanel();
        var bar = new TitleBar { Title = "Mesh Control", ShowMinMax = false };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);
        dialog.Content = root;

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            dialog.Close();
        };
        return dialog;
    }

    private static void AddButtons(Window dialog, StackPanel buttons, string accept, bool danger, Action onAccept)
    {
        var cancel = new Button { Content = "Annuler", Padding = new Thickness(16, 8) };
        cancel.Classes.Add("tool");
        cancel.Click += (_, _) => dialog.Close();

        var ok = new Button { Content = accept, Padding = new Thickness(18, 8), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Classes.Add("primary");
        if (danger) ok.Background = Palette.Resource("DangerBrush");
        ok.Click += (_, _) => onAccept();

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
    }
}
