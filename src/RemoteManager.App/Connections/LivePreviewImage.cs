using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RemoteManager.App.Connections;

/// <summary>
/// Live previews of open sessions: scaled down to what a tile shows (so memory and disk stay small) and
/// written over the connection's preview file. Encoding runs on the thread pool; only the write is on the caller.
/// </summary>
internal static class LivePreviewImage
{
    /// <summary>Twice the tile picture (264 x 140), so it stays sharp at 200 % scaling.</summary>
    public static readonly PixelSize TargetSize = new(528, 280);

    /// <summary>A PNG of a BGRA frame (RDP capture), scaled to cover <see cref="TargetSize"/>.</summary>
    public static Task<byte[]> EncodeAsync(byte[] bgra, int width, int height) => Task.Run(() =>
    {
        GCHandle handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var full = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(),
                new PixelSize(width, height), new Vector(96, 96), width * 4);
            return Encode(full);
        }
        finally
        {
            handle.Free();
        }
    });

    /// <summary>A PNG of <paramref name="frame"/> (VNC), scaled to cover <see cref="TargetSize"/>; disposes the frame.</summary>
    public static Task<byte[]> EncodeAsync(Bitmap frame) => Task.Run(() =>
    {
        using (frame)
            return Encode(frame);
    });

    /// <summary>Replaces the file in one step, so the overview never reads a half-written picture.</summary>
    public static void Write(string path, byte[] png)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".live";
        File.WriteAllBytes(temporary, png);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The size that covers <see cref="TargetSize"/> like the tile's UniformToFill, never larger than the source.</summary>
    public static PixelSize ScaledSize(PixelSize source)
    {
        double scale = Math.Min(1, Math.Max((double)TargetSize.Width / source.Width, (double)TargetSize.Height / source.Height));
        return new PixelSize(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
    }

    private static byte[] Encode(Bitmap source)
    {
        PixelSize size = ScaledSize(source.PixelSize);
        using var stream = new MemoryStream();
        if (size == source.PixelSize)
        {
            source.Save(stream, new PngBitmapEncoderOptions());
        }
        else
        {
            using Bitmap scaled = source.CreateScaledBitmap(size, BitmapInterpolationMode.MediumQuality);
            scaled.Save(stream, new PngBitmapEncoderOptions());
        }

        return stream.ToArray();
    }
}
