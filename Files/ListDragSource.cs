using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MeshControl.Files;

/// <summary>
/// Fait d'une liste une source de glisser-déposer.
///
/// WPF réduit une sélection multiple à l'élément cliqué dès l'appui du bouton :
/// on ne pourrait alors jamais glisser plusieurs fichiers d'un coup. L'appui sur
/// un élément déjà sélectionné est donc mis en attente, et ne resélectionne cet
/// élément seul qu'au relâchement — si aucun glisser n'a commencé entre-temps.
/// </summary>
internal sealed class ListDragSource
{
    private readonly ListView _list;
    private readonly Func<DataObject?> _createData;

    private Point? _pressPoint;
    private ListViewItem? _pressedItem;
    private bool _selectionDeferred;

    /// <param name="createData">
    /// Données à glisser pour la sélection courante, ou null s'il n'y a rien à
    /// glisser (glisser désactivé, sélection vide…).
    /// </param>
    public ListDragSource(ListView list, Func<DataObject?> createData)
    {
        _list = list;
        _createData = createData;

        list.PreviewMouseLeftButtonDown += OnPress;
        list.PreviewMouseLeftButtonUp += OnRelease;
        list.PreviewMouseMove += OnMove;
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        _pressedItem = ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource)
                       as ListViewItem;
        _pressPoint = _pressedItem is null ? null : e.GetPosition(_list);
        _selectionDeferred = false;

        if (_pressedItem is { IsSelected: true } && e.ClickCount == 1
            && Keyboard.Modifiers == ModifierKeys.None && _list.SelectedItems.Count > 1)
        {
            _selectionDeferred = true;
            e.Handled = true;
            _list.Focus();
        }
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (_selectionDeferred && _pressedItem is not null)
        {
            _list.SelectedItems.Clear();
            _pressedItem.IsSelected = true;
        }
        Reset();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressPoint is not { } start) return;

        var delta = e.GetPosition(_list) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        Reset();
        if (_createData() is { } data)
            DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy);
    }

    private void Reset()
    {
        _pressPoint = null;
        _pressedItem = null;
        _selectionDeferred = false;
    }
}
