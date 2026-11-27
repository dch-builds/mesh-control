using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MeshControl.UI;

/// <summary>
/// Transitions courtes de l'interface (120 à 220 ms) : apparition en fondu,
/// léger glissement, rebond d'une pastille, ouverture d'un volet.
///
/// Elles suivent le réglage Windows « Afficher les animations » : coupé, tout
/// redevient instantané. Une animation ne laisse jamais de valeur figée
/// derrière elle (FillBehavior.Stop + valeur de base posée à la fin), sans quoi
/// un redimensionnement ou un changement d'état ultérieur resterait sans effet.
/// </summary>
public static class Motion
{
    /// <summary>Animations activées dans Windows (Paramètres › Accessibilité › Effets visuels).</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static IEasingFunction Freeze(CubicEase ease)
    {
        ease.Freeze();
        return ease;
    }

    private static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));

    /// <summary>Fait apparaître un élément en fondu, avec un léger glissement vertical.</summary>
    public static void FadeIn(UIElement element, double fromOpacity = 0, int ms = 160, double slideY = 0)
    {
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(fromOpacity, 1, Ms(ms)) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });

        if (slideY == 0) return;
        var translate = TranslateOf(element);
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(slideY, 0, Ms(ms)) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Petit rebond d'un élément cliqué (pastille de filtre).</summary>
    public static void Pop(FrameworkElement element, int ms = 170)
    {
        if (!Enabled) return;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        if (element.RenderTransform is not ScaleTransform scale)
            element.RenderTransform = scale = new ScaleTransform();

        var animation = new DoubleAnimation(0.92, 1, Ms(ms)) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>
    /// Anime la largeur d'un volet de <paramref name="from"/> à <paramref name="to"/>,
    /// puis pose <paramref name="to"/> comme valeur de base et appelle
    /// <paramref name="completed"/>.
    /// </summary>
    public static void Width(FrameworkElement element, double from, double to, int ms = 180, Action? completed = null)
    {
        if (!Enabled)
        {
            element.Width = to;
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation(from, to, Ms(ms)) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) =>
        {
            element.Width = to;
            completed?.Invoke();
            // Le fondu est tenu jusqu'ici : relâché plus tôt, un volet qui se
            // ferme réapparaîtrait le temps d'une image avant d'être masqué.
            element.BeginAnimation(UIElement.OpacityProperty, null);
        };
        element.Width = to;
        element.BeginAnimation(FrameworkElement.WidthProperty, animation);
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(from < to ? 0 : 1, from < to ? 1 : 0, Ms(ms)) { FillBehavior = FillBehavior.HoldEnd });
    }

    /// <summary>
    /// Pulsation lente (squelette de chargement) qui ne tourne que tant que
    /// l'élément est visible. Une animation « pour toujours » continue sinon de
    /// battre une fois l'élément masqué, et oblige WPF à redessiner la fenêtre
    /// à chaque image : mesuré, 9 % d'un cœur de processeur au repos.
    /// </summary>
    public static void PulseWhileVisible(UIElement element)
    {
        void Update()
        {
            if (element.IsVisible && Enabled)
                element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.45, Ms(800))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                });
            else
                element.BeginAnimation(UIElement.OpacityProperty, null);
        }

        element.IsVisibleChanged += (_, _) => Update();
        Update();
    }

    /// <summary>
    /// Barre de progression indéterminée animée seulement quand elle est
    /// visible : même piège que la pulsation, son reflet tournait en continu
    /// derrière un panneau masqué.
    /// </summary>
    public static void IndeterminateWhileVisible(System.Windows.Controls.ProgressBar bar)
    {
        void Update() => bar.IsIndeterminate = bar.IsVisible;
        bar.IsVisibleChanged += (_, _) => Update();
        Update();
    }

    /// <summary>TranslateTransform de l'élément, créée au besoin sans écraser une transformation existante.</summary>
    private static TranslateTransform TranslateOf(UIElement element)
    {
        switch (element.RenderTransform)
        {
            case TranslateTransform existing:
                return existing;
            case TransformGroup group when group.Children.OfType<TranslateTransform>().FirstOrDefault() is { } inGroup:
                return inGroup;
            case null:
            case MatrixTransform { Matrix.IsIdentity: true }:
                var created = new TranslateTransform();
                element.RenderTransform = created;
                return created;
            default:
                var wrapped = new TransformGroup();
                wrapped.Children.Add(element.RenderTransform);
                var translate = new TranslateTransform();
                wrapped.Children.Add(translate);
                element.RenderTransform = wrapped;
                return translate;
        }
    }
}
