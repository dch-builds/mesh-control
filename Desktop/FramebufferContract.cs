namespace MeshControl.Desktop;

/// <summary>
/// Une tuile déjà décodée en pixels BGRA (prémultipliés, opaques), prête à
/// être copiée.
///
/// <c>Pixels</c> est un tableau LOUÉ au pool partagé : il est plus grand que
/// nécessaire et doit être rendu après usage. <see cref="IKvmFramebuffer.Apply"/>
/// s'en charge — ne pas conserver de référence au tableau au-delà de cet appel.
/// </summary>
public readonly record struct DecodedTile(byte[] Pixels, int Stride, int Width, int Height, int X, int Y);

/// <summary>
/// Le framebuffer du bureau distant, vu depuis la session.
///
/// Le décodage des images et la surface d'affichage dépendent du socle
/// graphique (WPF sous Windows, Avalonia/Skia sous macOS) : chacun fournit son
/// implémentation, la session KVM n'en connaît que ce contrat.
/// </summary>
public interface IKvmFramebuffer
{
    int Width { get; }
    int Height { get; }

    /// <summary>
    /// Décode une tuile (JPEG/PNG/WebP) en pixels BGRA. Appelé sur le fil de
    /// réception : ne doit pas toucher à la surface d'affichage. Renvoie null si
    /// la tuile est illisible ou hors limites (voir <see cref="FramebufferLimits"/>).
    /// </summary>
    DecodedTile? Decode(byte[] imageData, int x, int y);

    /// <summary>Recrée la surface à la nouvelle taille. Thread de l'interface.</summary>
    void Resize(int width, int height);

    /// <summary>
    /// Copie un lot de tuiles décodées dans la surface, puis rend leurs tampons
    /// au pool — y compris en cas d'échec. Thread de l'interface.
    /// </summary>
    void Apply(IReadOnlyList<DecodedTile> tiles);
}

/// <summary>Bornes communes aux implémentations de <see cref="IKvmFramebuffer"/>.</summary>
public static class FramebufferLimits
{
    /// <summary>
    /// Plus grande dimension acceptée, pour l'écran comme pour une tuile : au-delà
    /// de deux écrans 8K côte à côte, la valeur ne peut venir que d'un agent
    /// défaillant ou hostile, et ferait allouer des gigaoctets.
    /// </summary>
    public const int MaxDimension = 16384;

    /// <summary>
    /// Taille maximale d'une tuile décodée : un écran 8K entier (130 Mo) passe,
    /// pas davantage.
    /// </summary>
    public const long MaxTileBytes = 7680L * 4320 * 4;

    /// <summary>Vrai si une tuile de ces dimensions peut être décodée sans risque.</summary>
    public static bool IsAcceptableTile(int width, int height) =>
        width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension
        && (long)width * height * 4 <= MaxTileBytes;

    /// <summary>Vrai si l'écran annoncé a des dimensions plausibles.</summary>
    public static bool IsAcceptableScreen(int width, int height) =>
        width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension;
}
