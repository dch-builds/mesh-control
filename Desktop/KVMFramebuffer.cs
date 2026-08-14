using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeshControl.Desktop;

/// <summary>Une tuile déjà décodée en pixels BGRA, prête à être copiée (rapide).</summary>
public readonly record struct DecodedTile(byte[] Pixels, int Stride, int Width, int Height, int X, int Y);

/// <summary>
/// Le framebuffer du bureau distant (WriteableBitmap BGRA32).
///
/// Performance : le DÉCODAGE JPEG (coûteux) se fait sur le thread de fond via
/// <see cref="Decode"/>, et seule la COPIE de pixels (légère) via <see cref="Apply"/>
/// touche au WriteableBitmap sur le thread UI. Cela évite de saturer l'UI sous
/// le flot de tuiles — le principal levier de fluidité côté client.
///
/// WriteableBitmap a son origine en haut à gauche : il est
/// top-left. On dessine donc la tuile directement à (x, y), sans inversion.
/// </summary>
public sealed class KVMFramebuffer
{
    public WriteableBitmap? Bitmap { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>À appeler sur le thread UI (crée le WriteableBitmap).</summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        Width = width;
        Height = height;
        Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        FillBackground();
    }

    private void FillBackground()
    {
        if (Bitmap is null) return;
        int w = Width, h = Height;
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = 0x17; // B
            pixels[i + 1] = 0x12; // G
            pixels[i + 2] = 0x0F; // R
            pixels[i + 3] = 0xFF; // A
        }
        Bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
    }

    /// <summary>
    /// Décode une tuile (JPEG/PNG/WebP) en pixels BGRA. Peut s'exécuter sur
    /// n'importe quel thread (aucun accès au WriteableBitmap ici). Renvoie null
    /// si la tuile est illisible.
    /// </summary>
    public static DecodedTile? Decode(byte[] imageData, int x, int y)
    {
        BitmapSource frame;
        try
        {
            using var ms = new MemoryStream(imageData);
            var decoder = BitmapDecoder.Create(
                ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }
        catch { return null; }

        int tw = frame.PixelWidth;
        int th = frame.PixelHeight;
        if (tw <= 0 || th <= 0) return null;

        BitmapSource bgra = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        int stride = tw * 4;
        var buffer = new byte[stride * th];
        bgra.CopyPixels(buffer, stride, 0);
        // Freeze n'est pas nécessaire : on ne transporte que le tableau d'octets.
        return new DecodedTile(buffer, stride, tw, th, x, y);
    }

    /// <summary>
    /// Copie une tuile déjà décodée dans le framebuffer. À appeler sur le thread
    /// UI. Clipping robuste : garantit que WritePixels reste dans les bornes du
    /// bitmap (un dépassement lèverait une ArgumentException).
    /// </summary>
    public void Apply(DecodedTile tile)
    {
        if (Bitmap is null) return;

        int srcX = tile.X < 0 ? -tile.X : 0;
        int srcY = tile.Y < 0 ? -tile.Y : 0;
        int destX = Math.Max(0, tile.X);
        int destY = Math.Max(0, tile.Y);

        int drawW = Math.Min(tile.Width - srcX, Width - destX);
        int drawH = Math.Min(tile.Height - srcY, Height - destY);
        if (drawW <= 0 || drawH <= 0) return;

        var sourceRect = new Int32Rect(srcX, srcY, drawW, drawH);
        Bitmap.WritePixels(sourceRect, tile.Pixels, tile.Stride, destX, destY);
    }
}
