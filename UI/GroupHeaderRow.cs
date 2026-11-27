using System.ComponentModel;

namespace MeshControl.UI;

/// <summary>
/// En-tête de dossier dans la liste des appareils, affiché comme une ligne.
///
/// La liste était groupée par WPF (CollectionView + GroupStyle + Expander).
/// Mesuré sur un parc de 1 341 machines, chaque filtrage, tri ou recherche y
/// coûtait 150 à 380 ms d'affichage : WPF régénère tous ses groupes à chaque
/// changement de source. Une liste plate, dont les en-têtes sont de simples
/// éléments, s'affiche en quelques dizaines de millisecondes ; le repli d'un
/// dossier revient à ne pas y mettre ses machines.
///
/// L'instance est conservée d'un filtrage à l'autre (voir MainWindow) : son
/// compteur et son état replié se mettent à jour en place.
/// </summary>
public sealed class GroupHeaderRow : INotifyPropertyChanged
{
    public GroupHeaderRow(string name) => Name = name;

    public string Name { get; }

    /// <summary>Distingue l'en-tête d'une machine dans le style commun des lignes.</summary>
    public bool IsHeader => true;

    private int _count;
    /// <summary>Machines du dossier dans la liste filtrée, repliées comprises.</summary>
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            Notify(nameof(Count));
        }
    }

    private bool _isCollapsed;
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (_isCollapsed == value) return;
            _isCollapsed = value;
            Notify(nameof(IsCollapsed));
            Notify(nameof(ChevronGlyph));
        }
    }

    /// <summary>Segoe MDL2 Assets : E70D chevron bas (déplié), E76C chevron droit (replié).</summary>
    public string ChevronGlyph => IsCollapsed ? "" : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
