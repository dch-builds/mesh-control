using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeshControl.Desktop;

/// <summary>
/// Une tuile déjà décodée en pixels BGRA, prête à être copiée (rapide).
///
/// <c>Pixels</c> est un tableau LOUÉ au pool partagé : il est plus grand que
/// nécessaire et doit être rendu après usage. <see cref="KVMFramebuffer.Apply"/>
/// s'en charge — ne pas conserver de référence au tableau au-delà de cet appel.
/// </summary>
public readonly record struct DecodedTile(byte[] Pixels, int Stride, int Width, int Height, int X, int Y);

/// <summary>
/// Le framebuffer du bureau distant (WriteableBitmap BGRA32).
///
/// Performance : le DÉCODAGE JPEG (coûteux) se fait sur le thread de fond via
/// <see cref="Decode"/>, et seule la COPIE de pixels (légère) via <see cref="Apply"/>
/// touche au WriteableBitmap sur le thread UI, par lots. Cela évite de saturer
/// l'UI sous le flot de tuiles — le principal levier de fluidité côté client.
///
/// WriteableBitmap a son origine en haut à gauche : il est
/// top-left. On dessine donc la tuile directement à (x, y), sans inversion.
/// </summary>
public sealed class KVMFramebuffer
{
    public WriteableBitmap? Bitmap { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>
    /// Plus grande dimension acceptée, pour l'écran comme pour une tuile : au-delà
    /// de deux écrans 8K côte à côte, la valeur ne peut venir que d'un agent
    /// défaillant ou hostile, et ferait allouer des gigaoctets.
    /// </summary>
    public const int MaxDimension = 16384;

    /// <summary>À appeler sur le thread UI (crée le WriteableBitmap).</summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension) return;

        // Le bitmap d'abord, les dimensions ensuite : si la création échoue,
        // l'ancien bitmap et ses dimensions restent cohérents. L'inverse
        // laissait de grandes dimensions sur un petit bitmap, et la copie des
        // tuiles écrivait hors de sa mémoire.
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        Bitmap = bitmap;
        Width = width;
        Height = height;
        FillBackground();
    }

    private void FillBackground()
    {
        var bitmap = Bitmap;
        if (bitmap is null) return;

        // Une seule ligne, recopiée sur toute la hauteur : un tableau de la
        // taille de l'écran pesait 8 Mo en 1080p et 33 Mo en 4K, alloué dans le
        // tas des gros objets à chaque changement de résolution.
        var row = new byte[Width * 4];
        for (int i = 0; i < row.Length; i += 4)
        {
            row[i + 0] = 0x17; // B
            row[i + 1] = 0x12; // G
            row[i + 2] = 0x0F; // R
            row[i + 3] = 0xFF; // A
        }

        bitmap.Lock();
        try
        {
            for (int y = 0; y < Height; y++)
                Marshal.Copy(row, 0, bitmap.BackBuffer + (nint)y * bitmap.BackBufferStride, row.Length);
            bitmap.AddDirtyRect(new Int32Rect(0, 0, Width, Height));
        }
        finally
        {
            bitmap.Unlock();
        }
    }

    /// <summary>
    /// Décode une tuile (JPEG/PNG/WebP) en pixels BGRA. Peut s'exécuter sur
    /// n'importe quel thread (aucun accès au WriteableBitmap ici). Renvoie null
    /// si la tuile est illisible.
    /// </summary>
    /// <summary>
    /// Taille maximale d'une tuile décodée : un écran 8K entier (130 Mo) passe,
    /// pas davantage.
    /// </summary>
    private const long MaxTileBytes = 7680L * 4320 * 4;

    public static DecodedTile? Decode(byte[] imageData, int x, int y)
    {
        // Décodage différé (BitmapCacheOption.None) : les dimensions se lisent
        // dans l'en-tête, AVANT de décompresser. Une image de quelques octets
        // peut annoncer 30 000 × 30 000 pixels ; décodée d'emblée, elle aurait
        // englouti des gigaoctets avant le moindre contrôle.
        using var ms = new MemoryStream(imageData);
        BitmapSource frame;
        try
        {
            var decoder = BitmapDecoder.Create(
                ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            frame = decoder.Frames[0];
        }
        catch { return null; }

        int tw, th;
        try
        {
            tw = frame.PixelWidth;
            th = frame.PixelHeight;
        }
        catch { return null; }
        if (tw <= 0 || th <= 0 || tw > MaxDimension || th > MaxDimension) return null;
        if ((long)tw * th * 4 > MaxTileBytes) return null;

        int stride = tw * 4;

        // Tableau loué plutôt qu'alloué : sur un bureau actif, ce sont des
        // milliers d'allocations par minute (une par tuile) qui déclenchaient des
        // collections fréquentes, ressenties comme des micro-saccades. Le pool
        // renvoie un tableau au moins aussi grand — jamais exactement de la
        // taille demandée, d'où le champ Stride pour retrouver les lignes.
        var buffer = ArrayPool<byte>.Shared.Rent(stride * th);
        try
        {
            BitmapSource bgra = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            bgra.CopyPixels(buffer, stride, 0);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return null;
        }

        // Freeze n'est pas nécessaire : on ne transporte que le tableau d'octets.
        return new DecodedTile(buffer, stride, tw, th, x, y);
    }

    /// <summary>
    /// Copie une série de tuiles déjà décodées dans le framebuffer, en un seul
    /// verrouillage du bitmap. À appeler sur le thread UI.
    ///
    /// Un rafraîchissement complet arrive en centaines de tuiles : les copier
    /// une à une (WritePixels) verrouillait et invalidait le bitmap autant de
    /// fois. Ici, un seul Lock/Unlock, et une zone modifiée par tuile.
    ///
    /// Rend chaque tampon loué au pool, y compris en cas d'échec : sans cela le
    /// pool se viderait et on retomberait sur des allocations.
    /// </summary>
    public void Apply(IReadOnlyList<DecodedTile> tiles)
    {
        var bitmap = Bitmap;
        bool locked = false;
        try
        {
            if (bitmap is null || tiles.Count == 0) return;
            bitmap.Lock();
            locked = true;
            foreach (var tile in tiles) CopyTile(bitmap, tile);
        }
        finally
        {
            if (locked) bitmap!.Unlock();
            foreach (var tile in tiles) ArrayPool<byte>.Shared.Return(tile.Pixels);
        }
    }

    /// <summary>
    /// Copie une tuile dans le tampon arrière, bitmap verrouillé. Clipping
    /// robuste : la copie reste dans les bornes du bitmap, une tuile qui déborde
    /// (changement de résolution en vol) est rognée plutôt que d'écrire hors
    /// de la mémoire du tampon.
    /// </summary>
    private void CopyTile(WriteableBitmap bitmap, DecodedTile tile)
    {
        int srcX = tile.X < 0 ? -tile.X : 0;
        int srcY = tile.Y < 0 ? -tile.Y : 0;
        int destX = Math.Max(0, tile.X);
        int destY = Math.Max(0, tile.Y);

        // Rognage sur le bitmap réellement alloué, et non sur Width/Height :
        // c'est sa mémoire qu'il ne faut pas dépasser.
        int drawW = Math.Min(tile.Width - srcX, bitmap.PixelWidth - destX);
        int drawH = Math.Min(tile.Height - srcY, bitmap.PixelHeight - destY);
        if (drawW <= 0 || drawH <= 0) return;

        int destStride = bitmap.BackBufferStride;
        var dest = bitmap.BackBuffer + (nint)destY * destStride + (nint)destX * 4;
        int srcOffset = srcY * tile.Stride + srcX * 4;
        int rowBytes = drawW * 4;

        for (int row = 0; row < drawH; row++)
            Marshal.Copy(tile.Pixels, srcOffset + row * tile.Stride, dest + (nint)row * destStride, rowBytes);

        bitmap.AddDirtyRect(new Int32Rect(destX, destY, drawW, drawH));
    }
}
