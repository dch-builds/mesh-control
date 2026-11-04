using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MeshControl.UI;

/// <summary>
/// Carte à ombre portée dont le contenu échappe à l'effet.
///
/// Un effet posé sur un conteneur s'applique à tout ce qu'il contient : WPF
/// rastérise de nouveau l'ensemble, puis repasse l'ombre, à chaque changement
/// du contenu — défilement d'une liste, survol d'une ligne, barre de
/// progression. Ici l'ombre est portée par un visuel distinct, placé derrière
/// le contenu et de même forme : elle n'est recalculée que si la carte change
/// de taille.
///
/// S'emploie comme un Border : seule l'ombre, réglée par
/// <see cref="Shadow"/> (voir le style « Card »), lui est propre.
/// </summary>
public sealed class CardBorder : Border
{
    public static readonly DependencyProperty ShadowProperty =
        DependencyProperty.Register(nameof(Shadow), typeof(Effect), typeof(CardBorder),
            new PropertyMetadata(null, (d, e) => ((CardBorder)d)._shadow.Effect = (Effect?)e.NewValue));

    /// <summary>Effet d'ombre, appliqué au seul visuel de fond.</summary>
    public Effect? Shadow
    {
        get => (Effect?)GetValue(ShadowProperty);
        set => SetValue(ShadowProperty, value);
    }

    /// <summary>
    /// Reproduit le fond et le contour de la carte : il la recouvre exactement,
    /// et l'ombre a donc sa forme. Le contenu, dessiné après, passe au-dessus.
    /// </summary>
    private readonly Border _shadow = new() { IsHitTestVisible = false };

    public CardBorder()
    {
        Bind(BackgroundProperty);
        Bind(BorderBrushProperty);
        Bind(BorderThicknessProperty);
        Bind(CornerRadiusProperty);
        AddVisualChild(_shadow);
    }

    private void Bind(DependencyProperty property) =>
        _shadow.SetBinding(property, new Binding(property.Name) { Source = this });

    protected override int VisualChildrenCount => base.VisualChildrenCount + 1;

    // Le fond d'ombre en premier : les enfants suivants sont dessinés par-dessus.
    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _shadow : base.GetVisualChild(index - 1);

    protected override Size MeasureOverride(Size constraint)
    {
        _shadow.Measure(new Size(0, 0));
        return base.MeasureOverride(constraint);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        _shadow.Arrange(new Rect(arrangeSize));
        return base.ArrangeOverride(arrangeSize);
    }
}
