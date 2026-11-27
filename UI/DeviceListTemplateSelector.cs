using System.Windows;
using System.Windows.Controls;

namespace MeshControl.UI;

/// <summary>
/// Choisit le gabarit d'une ligne de la liste des appareils : en-tête de
/// dossier, machine en carte (barre latérale affichée) ou machine en colonnes
/// (barre latérale masquée). Changer de disposition remplace le sélecteur, ce
/// qui fait redessiner toutes les lignes avec le bon gabarit.
/// </summary>
public sealed class DeviceListTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Header { get; init; }
    public DataTemplate? Card { get; init; }
    public DataTemplate? Columns { get; init; }
    public bool UseColumns { get; init; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        GroupHeaderRow => Header,
        DeviceRow => UseColumns ? Columns : Card,
        _ => null,
    };
}
