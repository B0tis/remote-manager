// IconGen: renders src/RemoteManager.App/Assets/logo.svg (the source of truth) into
//   Assets/logo.ico       Windows icon with 16, 20, 24, 32, 40, 48, 64, 128 and 256 px frames, 32-bit with alpha
//                         (32-bit DIB frames below 256 px, PNG for 256 px). Exe icon, window icons, Velopack Setup.
//   Assets/logo-48.png    in-app logo, shown at 24 px (title row of the navigation pane), sharp up to 200 % scaling.
//   Assets/logo-96.png    in-app logo, shown at 48 px (About dialog), sharp up to 200 % scaling.
// Every size is rendered directly from the vector with anti-aliasing (no downscaling of a large bitmap); up to 48 px the
// white strokes are hinted to whole pixels (see SvgLogo.Render) so the small frames stay crisp.
//
// Run from the repository root (no global installs; SkiaSharp is the version Avalonia already uses):
//   dotnet run --project tools/IconGen
// Optional: dotnet run --project tools/IconGen -- <path-to-logo.svg>   (outputs go next to the SVG)
//
// The SVG reader only knows what the logo uses: <svg viewBox>, <defs>/<linearGradient> with <stop>, <rect> (rx/ry) and
// <path> with fill/stroke, stroke-width, stroke-linecap and stroke-linejoin. Anything else fails loudly, so a logo
// change that needs more SVG features cannot silently render wrong.
using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

string svgPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(FindRepoRoot(), "src", "RemoteManager.App", "Assets", "logo.svg"));
string outDir = Path.GetDirectoryName(svgPath)!;

var logo = SvgLogo.Load(svgPath);

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var frames = icoSizes.Select(size => (size, image: logo.Render(size))).ToList();
string icoPath = Path.Combine(outDir, "logo.ico");
File.WriteAllBytes(icoPath, IcoWriter.Write(frames));
Console.WriteLine($"{icoPath} ({string.Join(", ", icoSizes)} px)");

foreach (int size in new[] { 48, 96 })
{
    using SKImage image = logo.Render(size);
    string pngPath = Path.Combine(outDir, $"logo-{size}.png");
    File.WriteAllBytes(pngPath, Png(image));
    Console.WriteLine(pngPath);
}

foreach (var (_, image) in frames)
    image.Dispose();
return 0;

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "RemoteManager.slnx")))
            return dir.FullName;
    }
    throw new InvalidOperationException("Run from inside the repository (RemoteManager.slnx not found) or pass the SVG path.");
}

static byte[] Png(SKImage image)
{
    using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

/// <summary>The parsed logo: a list of shapes in viewBox coordinates.</summary>
internal sealed class SvgLogo
{
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    private readonly SKRect viewBox;
    private readonly List<Shape> shapes;

    private SvgLogo(SKRect viewBox, List<Shape> shapes)
    {
        this.viewBox = viewBox;
        this.shapes = shapes;
    }

    public static SvgLogo Load(string fileName)
    {
        XElement root = XDocument.Load(fileName).Root ?? throw new InvalidDataException("Empty SVG.");
        if (root.Name != Ns + "svg")
            throw new InvalidDataException($"Root element must be <svg>, got <{root.Name.LocalName}>.");

        float[] vb = Numbers(root.Attribute("viewBox")?.Value ?? throw new InvalidDataException("<svg> needs a viewBox."));
        var viewBox = SKRect.Create(vb[0], vb[1], vb[2], vb[3]);
        if (viewBox.Width != viewBox.Height)
            throw new NotSupportedException("The viewBox must be square (icons are square).");

        var gradients = new Dictionary<string, Gradient>(StringComparer.Ordinal);
        var shapes = new List<Shape>();
        foreach (XElement element in root.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "defs":
                    foreach (XElement def in element.Elements())
                    {
                        if (def.Name.LocalName != "linearGradient")
                            throw Unsupported(def);
                        gradients[Required(def, "id")] = Gradient.Parse(def);
                    }
                    break;
                case "rect":
                {
                    float x = Number(element, "x", 0), y = Number(element, "y", 0);
                    float w = Number(element, "width"), h = Number(element, "height");
                    float rx = Number(element, "rx", float.NaN), ry = Number(element, "ry", float.NaN);
                    if (float.IsNaN(rx)) rx = float.IsNaN(ry) ? 0 : ry;
                    if (float.IsNaN(ry)) ry = rx;
                    rx = Math.Min(rx, w / 2);
                    ry = Math.Min(ry, h / 2);
                    shapes.Add(Shape.Parse(element, map =>
                    {
                        SKPoint a = map(new SKPoint(x, y)), b = map(new SKPoint(x + w, y + h));
                        var path = new SKPath();
                        path.AddRoundRect(new SKRect(a.X, a.Y, b.X, b.Y), rx, ry);
                        return path;
                    }, gradients));
                    break;
                }
                case "path":
                {
                    SKPath path = SKPath.ParseSvgPathData(Required(element, "d"))
                        ?? throw new InvalidDataException($"Invalid path data: {element}");
                    shapes.Add(Shape.Parse(element, map => MapPoints(path, map), gradients));
                    break;
                }
                default:
                    throw Unsupported(element);
            }
        }
        return new SvgLogo(viewBox, shapes);
    }

    /// <summary>
    /// Renders the logo straight from the vector shapes into a size x size image (premultiplied BGRA). Up to
    /// <see cref="HintedMaxSize"/> px the strokes are hinted: width rounded to whole pixels and geometry snapped
    /// to the pixel grid, so the thin white lines stay sharp instead of smearing across two pixel rows.
    /// </summary>
    public SKImage Render(int size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        float scale = size / viewBox.Width;
        canvas.Scale(scale);
        canvas.Translate(-viewBox.Left, -viewBox.Top);
        foreach (Shape shape in shapes)
            shape.Draw(canvas, viewBox.Location, scale, hint: size <= HintedMaxSize);
        return surface.Snapshot();
    }

    public const int HintedMaxSize = 48;

    private static SKPath MapPoints(SKPath source, Func<SKPoint, SKPoint> map)
    {
        var path = new SKPath();
        using SKPath.RawIterator iterator = source.CreateRawIterator();
        var points = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: path.MoveTo(map(points[0])); break;
                case SKPathVerb.Line: path.LineTo(map(points[1])); break;
                case SKPathVerb.Quad: path.QuadTo(map(points[1]), map(points[2])); break;
                case SKPathVerb.Conic: path.ConicTo(map(points[1]), map(points[2]), iterator.ConicWeight()); break;
                case SKPathVerb.Cubic: path.CubicTo(map(points[1]), map(points[2]), map(points[3])); break;
                case SKPathVerb.Close: path.Close(); break;
            }
        }
        return path;
    }

    internal static Exception Unsupported(XElement element) =>
        new NotSupportedException($"IconGen does not support <{element.Name.LocalName}>; extend tools/IconGen/Program.cs.");

    internal static string Required(XElement element, string name) =>
        element.Attribute(name)?.Value ?? throw new InvalidDataException($"<{element.Name.LocalName}> needs {name}.");

    internal static float Number(XElement element, string name, float fallback = float.NaN)
    {
        string? text = element.Attribute(name)?.Value;
        if (text is null)
            return float.IsNaN(fallback) && name is "width" or "height" ? throw new InvalidDataException($"<{element.Name.LocalName}> needs {name}.") : fallback;
        return float.Parse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture) / (text.EndsWith('%') ? 100f : 1f);
    }

    private static float[] Numbers(string text) =>
        text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => float.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
}

/// <summary>A linearGradient in objectBoundingBox units (the SVG default).</summary>
internal sealed record Gradient(SKPoint Start, SKPoint End, SKColor[] Colors, float[] Offsets)
{
    public static Gradient Parse(XElement element)
    {
        if (element.Attribute("gradientUnits")?.Value is { } units && units != "objectBoundingBox")
            throw new NotSupportedException($"gradientUnits=\"{units}\" is not supported.");
        if (element.Attribute("gradientTransform") is not null || element.Attribute("href") is not null)
            throw new NotSupportedException("gradientTransform and href are not supported.");

        var stops = element.Elements().Select(stop =>
        {
            if (stop.Name.LocalName != "stop")
                throw SvgLogo.Unsupported(stop);
            SKColor color = Shape.Color(SvgLogo.Required(stop, "stop-color"));
            float opacity = SvgLogo.Number(stop, "stop-opacity", 1);
            return (offset: SvgLogo.Number(stop, "offset", 0), color: color.WithAlpha((byte)Math.Round(color.Alpha * opacity)));
        }).ToList();

        return new Gradient(
            new SKPoint(SvgLogo.Number(element, "x1", 0), SvgLogo.Number(element, "y1", 0)),
            new SKPoint(SvgLogo.Number(element, "x2", 1), SvgLogo.Number(element, "y2", 0)),
            stops.Select(s => s.color).ToArray(),
            stops.Select(s => s.offset).ToArray());
    }

    public SKShader CreateShader(SKRect bounds) => SKShader.CreateLinearGradient(
        new SKPoint(bounds.Left + Start.X * bounds.Width, bounds.Top + Start.Y * bounds.Height),
        new SKPoint(bounds.Left + End.X * bounds.Width, bounds.Top + End.Y * bounds.Height),
        Colors, Offsets, SKShaderTileMode.Clamp);
}

/// <summary>One filled and/or stroked path. The geometry is built with a point mapper (identity or pixel snapping).</summary>
internal sealed class Shape(Func<Func<SKPoint, SKPoint>, SKPath> geometry, object? fill, SKColor? stroke, float strokeWidth, SKStrokeCap cap, SKStrokeJoin join)
{
    public static Shape Parse(XElement element, Func<Func<SKPoint, SKPoint>, SKPath> geometry, Dictionary<string, Gradient> gradients)
    {
        foreach (XAttribute attribute in element.Attributes())
        {
            if (attribute.Name.LocalName is not ("x" or "y" or "width" or "height" or "rx" or "ry" or "d" or "fill"
                or "stroke" or "stroke-width" or "stroke-linecap" or "stroke-linejoin"))
                throw new NotSupportedException($"Attribute {attribute.Name.LocalName} on <{element.Name.LocalName}> is not supported.");
        }

        // SVG defaults: fill black, no stroke, width 1, butt caps, miter joins.
        string fillText = element.Attribute("fill")?.Value ?? "#000000";
        object? fill = fillText switch
        {
            "none" => null,
            _ when fillText.StartsWith("url(#", StringComparison.Ordinal) =>
                gradients.TryGetValue(fillText[5..^1], out Gradient? g) ? g : throw new InvalidDataException($"Unknown gradient {fillText}."),
            _ => Color(fillText),
        };
        string strokeText = element.Attribute("stroke")?.Value ?? "none";
        SKColor? stroke = strokeText == "none" ? null : Color(strokeText);

        SKStrokeCap cap = (element.Attribute("stroke-linecap")?.Value ?? "butt") switch
        {
            "butt" => SKStrokeCap.Butt, "round" => SKStrokeCap.Round, "square" => SKStrokeCap.Square,
            var other => throw new NotSupportedException($"stroke-linecap {other}"),
        };
        SKStrokeJoin join = (element.Attribute("stroke-linejoin")?.Value ?? "miter") switch
        {
            "miter" => SKStrokeJoin.Miter, "round" => SKStrokeJoin.Round, "bevel" => SKStrokeJoin.Bevel,
            var other => throw new NotSupportedException($"stroke-linejoin {other}"),
        };
        return new Shape(geometry, fill, stroke, SvgLogo.Number(element, "stroke-width", 1), cap, join);
    }

    public static SKColor Color(string text) =>
        SKColor.TryParse(text, out SKColor color) ? color : throw new NotSupportedException($"Color {text} is not supported.");

    public void Draw(SKCanvas canvas, SKPoint origin, float scale, bool hint)
    {
        // Hinting only applies to pure strokes (the white monitor and chevron); fills keep their exact geometry.
        float width = strokeWidth;
        Func<SKPoint, SKPoint> map = p => p;
        if (hint && stroke is not null && fill is null)
        {
            int pixels = Math.Max(1, (int)MathF.Round(strokeWidth * scale));
            width = pixels / scale;
            // Odd widths are centred on pixel centres, even widths on pixel edges.
            float offset = pixels % 2 == 1 ? 0.5f : 0f;
            float Snap(float value, float start) => (MathF.Round((value - start) * scale - offset) + offset) / scale + start;
            map = p => new SKPoint(Snap(p.X, origin.X), Snap(p.Y, origin.Y));
        }

        using SKPath path = geometry(map);
        if (fill is not null)
        {
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            if (fill is Gradient gradient)
                paint.Shader = gradient.CreateShader(path.Bounds);
            else
                paint.Color = (SKColor)fill;
            canvas.DrawPath(path, paint);
        }
        if (stroke is { } color)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color,
                StrokeWidth = width, StrokeCap = cap, StrokeJoin = join, StrokeMiter = 4,
            };
            canvas.DrawPath(path, paint);
        }
    }
}

/// <summary>Writes a multi-size .ico: 32-bit BGRA DIB frames (with AND mask) below 256 px, a PNG frame at 256 px.</summary>
internal static class IcoWriter
{
    public static byte[] Write(IReadOnlyList<(int size, SKImage image)> frames)
    {
        var payloads = frames.Select(f => f.size >= 256 ? PngFrame(f.image) : DibFrame(f.image)).ToList();

        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header[0..], 0); // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1); // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)frames.Count);
        stream.Write(header);

        int offset = 6 + 16 * frames.Count;
        Span<byte> entry = stackalloc byte[16];
        for (int i = 0; i < frames.Count; i++)
        {
            int size = frames[i].size;
            entry.Clear();
            entry[0] = (byte)(size >= 256 ? 0 : size); // width, 0 = 256
            entry[1] = (byte)(size >= 256 ? 0 : size); // height
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);  // planes
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32); // bits per pixel
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)payloads[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);
            stream.Write(entry);
            offset += payloads[i].Length;
        }
        foreach (byte[] payload in payloads)
            stream.Write(payload);
        return stream.ToArray();
    }

    private static byte[] PngFrame(SKImage image)
    {
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] DibFrame(SKImage image)
    {
        int size = image.Width;
        // Icon DIBs store straight (not premultiplied) BGRA, bottom-up.
        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (!image.ReadPixels(bitmap.PeekPixels()))
            throw new InvalidOperationException("ReadPixels failed.");
        byte[] pixels = bitmap.GetPixelSpan().ToArray();

        int maskStride = (size + 31) / 32 * 4;
        int xorSize = size * size * 4, andSize = maskStride * size;
        byte[] dib = new byte[40 + xorSize + andSize];
        Span<byte> h = dib.AsSpan(0, 40);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0..], 40);           // biSize
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], size);          // biWidth
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], size * 2);      // biHeight: XOR + AND
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], 1);           // biPlanes
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], 32);          // biBitCount
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)(xorSize + andSize)); // biSizeImage

        for (int y = 0; y < size; y++)
        {
            int src = y * size * 4, dstRow = size - 1 - y;
            pixels.AsSpan(src, size * 4).CopyTo(dib.AsSpan(40 + dstRow * size * 4));
            for (int x = 0; x < size; x++)
            {
                if (pixels[src + x * 4 + 3] == 0) // fully transparent: set the AND bit
                    dib[40 + xorSize + dstRow * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }
        return dib;
    }
}
