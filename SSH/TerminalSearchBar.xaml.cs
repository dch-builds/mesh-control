using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MeshControl.Ssh;

/// <summary>
/// Recherche dans l'historique d'un terminal. Partagée par le terminal via
/// agent et le terminal SSH, qui ont le même contrôle d'affichage en dessous.
/// </summary>
public partial class TerminalSearchBar : UserControl
{
    private TerminalControl? _terminal;

    public TerminalSearchBar() => InitializeComponent();

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Associe la barre au terminal qu'elle parcourt.</summary>
    public void Attach(TerminalControl terminal) => _terminal = terminal;

    /// <summary>
    /// Ouvre la barre. Le texte précédent est conservé mais sélectionné : on
    /// relance la même recherche d'une touche, ou on la remplace en tapant.
    /// </summary>
    public void Open()
    {
        Visibility = Visibility.Visible;
        Run();

        // Différé : au sortir de Collapsed, WPF n'a pas encore refait la mise
        // en page et Focus() échouerait en silence.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            QueryBox.Focus();
            QueryBox.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Ferme la barre et rend le clavier au terminal. Les surbrillances
    /// disparaissent : les laisser ferait croire à une recherche en cours.
    /// </summary>
    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _terminal?.Search(null);
        _terminal?.Focus();
    }

    /// <summary>Ouvre, ou ferme si la barre est déjà ouverte.</summary>
    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    /// <summary>
    /// Relance la recherche. Faite à chaque frappe : parcourir quelques milliers
    /// de lignes est instantané, et voir les occurrences apparaître au fil de la
    /// saisie évite d'avoir à valider pour savoir si l'on cherche la bonne chose.
    /// </summary>
    private void Run()
    {
        if (_terminal is null) return;

        _terminal.Search(QueryBox.Text);
        UpdateCount();
    }

    private void UpdateCount()
    {
        if (_terminal is null) return;

        var total = _terminal.MatchCount;
        CountText.Text = QueryBox.Text.Length == 0
            ? ""
            : total == 0
                ? "Aucun résultat"
                : $"{_terminal.CurrentMatchNumber} / {total}";

        PreviousButton.IsEnabled = NextButton.IsEnabled = total > 0;
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e) => Run();

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Previous_Click(sender, e);
                else Next_Click(sender, e);
                break;

            case Key.Escape:
                Close();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        _terminal?.FindPrevious();
        UpdateCount();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _terminal?.FindNext();
        UpdateCount();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
