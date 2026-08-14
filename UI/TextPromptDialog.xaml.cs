using System.Windows;
using System.Windows.Input;

namespace MeshControl.UI;

/// <summary>Invite de saisie d'une ligne de texte (nom de dossier, renommage…).</summary>
public partial class TextPromptDialog : Window
{
    public string? Value { get; private set; }

    /// <summary>
    /// Vrai pour une saisie libre (URL…). Par défaut la validation interdit les
    /// caractères illégaux dans un nom de fichier.
    /// </summary>
    private readonly bool _allowAnyCharacter;

    public TextPromptDialog(string title, string prompt, string initialValue = "",
                            string confirmLabel = "Valider", bool allowAnyCharacter = false)
    {
        _allowAnyCharacter = allowAnyCharacter;
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;
        ConfirmButton.Content = confirmLabel;

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            // Présélectionner le nom sans son extension, comme l'explorateur.
            var dot = initialValue.LastIndexOf('.');
            if (dot > 0) ValueBox.Select(0, dot);
            else ValueBox.SelectAll();
        };
    }

    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Confirm_Click(sender, e);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var value = ValueBox.Text.Trim();
        if (string.IsNullOrEmpty(value))
        {
            ShowError("Saisissez un nom.");
            return;
        }
        // Caractères interdits dans un nom de fichier, sur Windows comme sur Linux.
        if (!_allowAnyCharacter
            && value.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            ShowError("Le nom ne peut pas contenir : / \\ : * ? \" < > |");
            return;
        }

        Value = value;
        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        ValueBox.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
