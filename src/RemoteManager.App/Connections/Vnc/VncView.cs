using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MarcusW.VncClient;
using MarcusW.VncClient.Output;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Rendering;
using IRenderTarget = MarcusW.VncClient.Rendering.IRenderTarget;
using Screen = MarcusW.VncClient.Screen;
using VncPixelFormat = MarcusW.VncClient.PixelFormat;
using VncSize = MarcusW.VncClient.Size;

namespace RemoteManager.App.Connections.Vnc;

/// <summary>
/// Shows a VNC framebuffer and forwards mouse and keyboard to the server. The library decodes into a
/// <see cref="WriteableBitmap"/> on its own thread (<see cref="IRenderTarget"/>); this control draws it scaled to
/// the window (<see cref="ScaleToFit"/>) or 1:1, and maps pointer positions back to remote pixels.
/// Based on the MIT-licensed VncView of MarcusW.VncClient.Avalonia (its package targets Avalonia 11).
/// </summary>
public sealed class VncView : Control, IRenderTarget, IOutputHandler, IDisposable
{
    // Skia's BGRA layout, as the library expects it (from the original adapter's Conversions.cs).
    private static readonly VncPixelFormat Bgra = new("Skia kBGRA_8888_SkColorType", 32, 32, false, true, true, 0xFF, 0xFF, 0xFF, 0xFF, 16, 8, 0, 24);

    private readonly Lock bitmapLock = new();
    private readonly HashSet<KeySymbol> pressedKeys = [];
    private WriteableBitmap? bitmap;
    private RfbConnection? connection;
    private bool disposed;

    static VncView()
    {
        FocusableProperty.OverrideDefaultValue<VncView>(true);
    }

    public VncView()
    {
        ClipToBounds = true;
    }

    /// <summary>Raised on the UI thread after a frame landed (the self-test waits for it).</summary>
    public event EventHandler? FrameRendered;

    /// <summary>The attached connection; input goes there and frames come from there.</summary>
    public RfbConnection? Connection
    {
        get => connection;
        set
        {
            if (connection is not null)
            {
                ReleaseKeys();
                if (ReferenceEquals(connection.RenderTarget, this))
                    connection.RenderTarget = null;
                if (ReferenceEquals(connection.OutputHandler, this))
                    connection.OutputHandler = null;
            }

            connection = value;
            if (value is not null)
            {
                value.RenderTarget = this;
                value.OutputHandler = this;
            }
        }
    }

    /// <summary>No mouse or keyboard input is sent (the remote screen is only shown).</summary>
    public bool ViewOnly { get; set; }

    /// <summary>Scale the remote screen to the control (keeps the aspect ratio); otherwise 1:1.</summary>
    public bool ScaleToFit
    {
        get;
        set
        {
            field = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    } = true;

    public int FrameCount { get; private set; }

    /// <summary>Size of the remote screen in pixels (0 x 0 before the first frame).</summary>
    public PixelSize RemoteSize
    {
        get
        {
            lock (bitmapLock)
                return bitmap?.PixelSize ?? default;
        }
    }

    /// <summary>Copy of the current frame (for the preview tile and the self-test), or null.</summary>
    public Bitmap? Snapshot()
    {
        lock (bitmapLock)
        {
            if (bitmap is null)
                return null;
            using ILockedFramebuffer source = bitmap.Lock();
            return new Bitmap(source.Format, AlphaFormat.Opaque, source.Address, source.Size, source.Dpi, source.RowBytes);
        }
    }

    /// <summary>Color of a remote pixel as 0xRRGGBB (self-test), or null outside the frame.</summary>
    public uint? PixelAt(int x, int y)
    {
        lock (bitmapLock)
        {
            if (bitmap is null || x < 0 || y < 0 || x >= bitmap.PixelSize.Width || y >= bitmap.PixelSize.Height)
                return null;
            using ILockedFramebuffer frame = bitmap.Lock();
            return (uint)System.Runtime.InteropServices.Marshal.ReadInt32(frame.Address, y * frame.RowBytes + x * 4) & 0xFFFFFF;
        }
    }

    /// <summary>Sends Ctrl+Alt+Del (Windows logon screen, task manager).</summary>
    public void SendCtrlAltDel()
    {
        if (connection is null || ViewOnly)
            return;
        KeySymbol[] keys = [KeySymbol.Control_L, KeySymbol.Alt_L, KeySymbol.Delete];
        foreach (KeySymbol key in keys)
            connection.EnqueueMessage(new KeyEventMessage(true, key));
        foreach (KeySymbol key in keys.Reverse())
            connection.EnqueueMessage(new KeyEventMessage(false, key));
    }

    // --- IRenderTarget (library thread) -------------------------------------------------------------

    IFramebufferReference IRenderTarget.GrabFramebufferReference(VncSize size, IImmutableSet<Screen> layout)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var pixelSize = new PixelSize(size.Width, size.Height);
        WriteableBitmap target;
        bool resized;
        lock (bitmapLock)
        {
            resized = bitmap is null || bitmap.PixelSize != pixelSize;
            if (resized)
            {
                bitmap?.Dispose();
                bitmap = new WriteableBitmap(pixelSize, new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }

            target = bitmap!;
        }

        return new FramebufferReference(target.Lock(), () => Dispatcher.UIThread.Post(() =>
        {
            if (resized)
                InvalidateMeasure();
            InvalidateVisual();
            FrameCount++;
            FrameRendered?.Invoke(this, EventArgs.Empty);
        }));
    }

    // --- IOutputHandler ---------------------------------------------------------------------------------

    void IOutputHandler.RingBell()
    {
    }

    void IOutputHandler.HandleServerClipboardUpdate(string text)
    {
        // Text the remote side copied. Not shown or logged; only handed to the local clipboard.
        Dispatcher.UIThread.Post(async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        });
    }

    // --- Layout and drawing -------------------------------------------------------------------------

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize)
    {
        PixelSize remote = RemoteSize;
        if (ScaleToFit || remote.Width == 0)
        {
            return new Avalonia.Size(double.IsInfinity(availableSize.Width) ? remote.Width : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? remote.Height : availableSize.Height);
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return new Avalonia.Size(remote.Width / scaling, remote.Height / scaling);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        lock (bitmapLock)
        {
            if (bitmap is null)
                return;
            context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height), TargetRect(bitmap.PixelSize));
        }
    }

    /// <summary>Where the remote screen is drawn, in control coordinates.</summary>
    private Rect TargetRect(PixelSize remote)
    {
        if (remote.Width == 0 || remote.Height == 0)
            return default;
        if (!ScaleToFit)
        {
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            return new Rect(0, 0, remote.Width / scaling, remote.Height / scaling);
        }

        double factor = Math.Min(Bounds.Width / remote.Width, Bounds.Height / remote.Height);
        double width = remote.Width * factor, height = remote.Height * factor;
        return new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
    }

    /// <summary>Remote pixel under <paramref name="point"/> (clamped to the screen).</summary>
    public Position ToRemote(Point point)
    {
        PixelSize remote = RemoteSize;
        Rect target = TargetRect(remote);
        if (target.Width <= 0 || target.Height <= 0)
            return new Position(0, 0);
        int x = (int)((point.X - target.X) * remote.Width / target.Width);
        int y = (int)((point.Y - target.Y) * remote.Height / target.Height);
        return new Position(Math.Clamp(x, 0, remote.Width - 1), Math.Clamp(y, 0, remote.Height - 1));
    }

    // --- Mouse -----------------------------------------------------------------------------------------

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        e.Handled |= SendPointer(e.GetCurrentPoint(this), Vector.Zero);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Handled |= SendPointer(e.GetCurrentPoint(this), Vector.Zero);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Handled |= SendPointer(e.GetCurrentPoint(this), Vector.Zero);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled |= SendPointer(e.GetCurrentPoint(this), e.Delta);
    }

    private bool SendPointer(PointerPoint point, Vector wheel)
    {
        if (connection is null || ViewOnly)
            return false;

        Position position = ToRemote(point.Position);
        PointerPointProperties properties = point.Properties;
        MouseButtons buttons = (properties.IsLeftButtonPressed ? MouseButtons.Left : MouseButtons.None)
            | (properties.IsMiddleButtonPressed ? MouseButtons.Middle : MouseButtons.None)
            | (properties.IsRightButtonPressed ? MouseButtons.Right : MouseButtons.None);
        MouseButtons wheelButtons = (wheel.X > 0 ? MouseButtons.WheelRight : wheel.X < 0 ? MouseButtons.WheelLeft : MouseButtons.None)
            | (wheel.Y > 0 ? MouseButtons.WheelUp : wheel.Y < 0 ? MouseButtons.WheelDown : MouseButtons.None);

        // A wheel step is a press and release of the wheel "button".
        if (wheelButtons != MouseButtons.None)
            connection.EnqueueMessage(new PointerEventMessage(position, buttons | wheelButtons));
        connection.EnqueueMessage(new PointerEventMessage(position, buttons));
        return true;
    }

    // --- Keyboard --------------------------------------------------------------------------------------

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || connection is null || ViewOnly || e.Text is null)
            return;

        foreach (char c in e.Text)
        {
            KeySymbol symbol = VncKeyMapping.GetSymbolFromChar(c);
            if (!connection.EnqueueMessage(new KeyEventMessage(true, symbol)))
                break;
            connection.EnqueueMessage(new KeyEventMessage(false, symbol));
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key != Key.None)
            e.Handled = SendKey(true, e.Key, e.KeyModifiers);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!e.Handled && e.Key != Key.None)
            e.Handled = SendKey(false, e.Key, e.KeyModifiers);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        ReleaseKeys();
    }

    private bool SendKey(bool down, Key key, KeyModifiers modifiers)
    {
        if (connection is null || ViewOnly)
            return false;

        // With Ctrl held there is no text input, so printable keys go out as key events here.
        KeySymbol symbol = VncKeyMapping.GetSymbolFromKey(key, (modifiers & KeyModifiers.Control) != 0);
        if (symbol == KeySymbol.Null)
            return false;

        bool queued = connection.EnqueueMessage(new KeyEventMessage(down, symbol));
        if (down && queued)
            pressedKeys.Add(symbol);
        else if (!down)
            pressedKeys.Remove(symbol);
        return queued;
    }

    /// <summary>Releases keys still held on the server (focus lost, connection swapped).</summary>
    private void ReleaseKeys()
    {
        if (connection is not null)
        {
            foreach (KeySymbol symbol in pressedKeys)
            {
                if (!connection.EnqueueMessage(new KeyEventMessage(false, symbol)))
                    break;
            }
        }

        pressedKeys.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Connection = null;
        lock (bitmapLock)
        {
            bitmap?.Dispose();
            bitmap = null;
        }
    }

    private sealed class FramebufferReference(ILockedFramebuffer framebuffer, Action rendered) : IFramebufferReference
    {
        private ILockedFramebuffer? locked = framebuffer;

        public nint Address => Locked.Address;
        public VncSize Size => new(Locked.Size.Width, Locked.Size.Height);
        public VncPixelFormat Format => Bgra;
        public double HorizontalDpi => Locked.Dpi.X;
        public double VerticalDpi => Locked.Dpi.Y;

        private ILockedFramebuffer Locked => locked ?? throw new ObjectDisposedException(nameof(FramebufferReference));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref locked, null) is not { } done)
                return;
            done.Dispose();
            rendered();
        }
    }
}
