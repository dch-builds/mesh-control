using System.Buffers;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MeshControl.Desktop;
using SkiaSharp;

namespace MeshControl.Mac;

/// <summary>
/// Framebuffer du bureau distant pour Avalonia : WriteableBitmap BGRA, tuiles
/// décodées par Skia (déjà embarqué par Avalonia, sous licence MIT).
///
/// Même découpage que la version WPF : le décodage JPEG, coûteux, se fait sur
/// le fil de réception (<see cref="Decode"/>) ; seule la copie des pixels,
/// légère, a lieu sur le thread de l'interface (<see cref="Apply"/>), par lots.
/// </summary>
public sealed class AvaloniaFramebuffer : IKvmFramebuffer
{
    public WriteableBitmap? Bitmap { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public void Resize(int width, int height)
    {
        if (!FramebufferLimits.IsAcceptableScreen(width, height)) return;

        // L'ancien bitmap n'est pas libéré ici : l'image l'affiche peut-être
        // encore jusqu'à ce que la fenêtre lui substitue le nouveau. Le
        // ramasse-miettes s'en charge — les changements de résolution sont rares.
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        Bitmap = bitmap;
        Width = width;
        Height = height;
        FillBackground(bitmap);
    }

    private static unsafe void FillBackground(WriteableBitmap bitmap)
    {
        // 0xAARRGGBB lu en petit-boutiste : octets B=17 G=12 R=0F A=FF, la
        // couleur de fond de la version Windows.
        const uint background = 0xFF0F1217;
        using var fb = bitmap.Lock();
        for (int y = 0; y < fb.Size.Height; y++)
            new Span<uint>((byte*)fb.Address + (nint)y * fb.RowBytes, fb.Size.Width).Fill(background);
    }

    /// <inheritdoc/>
    public unsafe DecodedTile? Decode(byte[] imageData, int x, int y)
    {
        // SKCodec lit les dimensions dans l'en-tête AVANT de décompresser :
        // une image de quelques octets peut annoncer 30 000 × 30 000 pixels.
        SKCodec? codec;
        try { codec = SKCodec.Create(new SKMemoryStream(imageData)); }
        catch { return null; }
        if (codec is null) return null;

        using (codec)
        {
            int tw = codec.Info.Width, th = codec.Info.Height;
            if (!FramebufferLimits.IsAcceptableTile(tw, th)) return null;

            var info = new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul);
            int stride = info.RowBytes;

            // Tampon loué (voir DecodedTile) : une allocation par tuile
            // provoquait des collections fréquentes, ressenties en saccades.
            var buffer = ArrayPool<byte>.Shared.Rent(stride * th);
            SKCodecResult result;
            try
            {
                fixed (byte* pixels = buffer)
                    result = codec.GetPixels(info, (IntPtr)pixels);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                return null;
            }

            // Une tuile tronquée reste affichable : la suivante corrigera.
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                ArrayPool<byte>.Shared.Return(buffer);
                return null;
            }
            return new DecodedTile(buffer, stride, tw, th, x, y);
        }
    }

    /// <inheritdoc/>
    public void Apply(IReadOnlyList<DecodedTile> tiles)
    {
        var bitmap = Bitmap;
        try
        {
            if (bitmap is null || tiles.Count == 0) return;
            using var fb = bitmap.Lock();
            foreach (var tile in tiles) CopyTile(fb, tile);
        }
        finally
        {
            foreach (var tile in tiles) ArrayPool<byte>.Shared.Return(tile.Pixels);
        }
    }

    /// <summary>
    /// Copie une tuile, rognée aux bornes du bitmap réellement alloué : une
    /// tuile qui déborde (changement de résolution en vol) ne doit pas écrire
    /// hors de sa mémoire.
    /// </summary>
    private static unsafe void CopyTile(ILockedFramebuffer fb, DecodedTile tile)
    {
        int srcX = tile.X < 0 ? -tile.X : 0;
        int srcY = tile.Y < 0 ? -tile.Y : 0;
        int destX = Math.Max(0, tile.X);
        int destY = Math.Max(0, tile.Y);

        int drawW = Math.Min(tile.Width - srcX, fb.Size.Width - destX);
        int drawH = Math.Min(tile.Height - srcY, fb.Size.Height - destY);
        if (drawW <= 0 || drawH <= 0) return;

        int rowBytes = drawW * 4;
        byte* dest = (byte*)fb.Address + (nint)destY * fb.RowBytes + (nint)destX * 4;

        fixed (byte* src = tile.Pixels)
        {
            byte* from = src + srcY * tile.Stride + srcX * 4;
            for (int row = 0; row < drawH; row++)
                Buffer.MemoryCopy(from + (nint)row * tile.Stride, dest + (nint)row * fb.RowBytes,
                                  rowBytes, rowBytes);
        }
    }
}
