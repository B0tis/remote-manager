using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RemoteManager.App.Connections.Rdp;

/// <summary>A captured BGRA frame of an RDP session.</summary>
internal sealed class SessionFrame(byte[] pixels, int width, int height, string method)
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public string Method { get; } = method;

    /// <summary>True when (almost) every sampled pixel has the same color, e.g. an all-black surface.</summary>
    public bool IsBlank
    {
        get
        {
            int first = BitConverter.ToInt32(Pixels, 0) & 0x00FFFFFF;
            int step = Math.Max(4, Pixels.Length / 4 / 2048 * 4);
            int differing = 0;
            for (int i = 0; i + 4 <= Pixels.Length; i += step)
            {
                if ((BitConverter.ToInt32(Pixels, i) & 0x00FFFFFF) != first && ++differing > 16)
                    return false;
            }

            return true;
        }
    }

    public void SavePng(string path)
    {
        GCHandle handle = GCHandle.Alloc(Pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(
                PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(),
                new PixelSize(Width, Height), new Vector(96, 96), Width * 4);
            bitmap.Save(path, new PngBitmapEncoderOptions());
        }
        finally
        {
            handle.Free();
        }
    }
}

/// <summary>
/// Copies the picture of an embedded RDP session. The control paints into an
/// "OPWindowClass" (Output Painter Child Window) below the host HWND, the same window
/// MsRdpEx's RdpView.cs copies from.
/// </summary>
internal static class SessionCapture
{
    private const uint PwRenderFullContent = 0x2;
    private const uint SrcCopy = 0x00CC0020;

    public static SessionFrame? Capture(nint hostWindow)
    {
        if (hostWindow == 0)
            return null;

        nint target = FindOutputPainter(hostWindow);
        if (target == 0)
            target = hostWindow;

        if (!GetClientRect(target, out Rect rect) || rect.Right <= 0 || rect.Bottom <= 0)
            return null;

        int width = rect.Right;
        int height = rect.Bottom;

        // PrintWindow with PW_RENDERFULLCONTENT also works while the window is covered or the
        // tab is hidden; BitBlt from the window DC only sees what is on screen. Try both.
        SessionFrame? frame = CaptureWith(target, width, height, usePrintWindow: true);
        if (frame is null || frame.IsBlank)
            frame = CaptureWith(target, width, height, usePrintWindow: false) ?? frame;

        return frame;
    }

    /// <summary>Copies a rectangle of the screen as currently composed (used by the airspace probes).</summary>
    public static SessionFrame? CaptureScreen(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        nint screenDc = GetDC(0);
        nint memoryDc = CreateCompatibleDC(screenDc);
        nint bitmap = CreateCompatibleBitmap(screenDc, width, height);
        nint previous = SelectObject(memoryDc, bitmap);
        try
        {
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SrcCopy))
                return null;
            SelectObject(memoryDc, previous);
            previous = 0;
            return ReadPixels(memoryDc, bitmap, width, height, "Screen");
        }
        finally
        {
            if (previous != 0)
                SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(0, screenDc);
        }
    }

    private static SessionFrame? ReadPixels(nint memoryDc, nint bitmap, int width, int height, string method)
    {
        var info = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // top-down
            Planes = 1,
            BitCount = 32,
        };
        byte[] pixels = new byte[width * height * 4];
        int lines = GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, 0);
        return lines == height ? new SessionFrame(pixels, width, height, method) : null;
    }

    private static SessionFrame? CaptureWith(nint window, int width, int height, bool usePrintWindow)
    {
        nint windowDc = GetDC(window);
        if (windowDc == 0)
            return null;

        nint memoryDc = CreateCompatibleDC(windowDc);
        nint bitmap = CreateCompatibleBitmap(windowDc, width, height);
        nint previous = SelectObject(memoryDc, bitmap);
        try
        {
            bool ok = usePrintWindow
                ? PrintWindow(window, memoryDc, PwRenderFullContent)
                : BitBlt(memoryDc, 0, 0, width, height, windowDc, 0, 0, SrcCopy);
            if (!ok)
                return null;

            SelectObject(memoryDc, previous);
            previous = 0;

            return ReadPixels(memoryDc, bitmap, width, height, usePrintWindow ? "PrintWindow" : "BitBlt");
        }
        finally
        {
            if (previous != 0)
                SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(window, windowDc);
        }
    }

    private static nint FindOutputPainter(nint root)
    {
        nint found = 0;
        EnumChildWindows(root, (child, _) =>
        {
            var name = new char[64];
            int length = GetClassNameW(child, name, name.Length);
            if (new string(name, 0, length) == "OPWindowClass")
            {
                found = child;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, [Out] char[] className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint dc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);
}
