// Draws the Kinora icon and writes the PNG sizes and the Windows .ico.
// The kinora was a flip-book movie viewer from the 1890s: a fan of frames, the front one
// lit up, with a play mark.
//
//   dotnet run --project tools/IconMaker -- <output folder>

using SkiaSharp;

var output = args.Length > 0 ? args[0] : Path.Combine("src", "Kinora", "Assets");
Directory.CreateDirectory(output);

int[] pngSizes = [16, 24, 32, 48, 64, 128, 256, 512, 1024];
int[] icoSizes = [16, 24, 32, 48, 64, 128, 256];

var pngs = new Dictionary<int, byte[]>();
foreach (var size in pngSizes)
{
    pngs[size] = Render(size);
    File.WriteAllBytes(Path.Combine(output, $"kinora-{size}.png"), pngs[size]);
}
WriteIco(Path.Combine(output, "kinora.ico"), icoSizes.Select(s => (s, pngs[s])).ToList());
WriteIcns(Path.Combine(output, "kinora.icns"), pngs);
Console.WriteLine($"Icon written to {Path.GetFullPath(output)}");

static byte[] Render(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    c.Scale(size / 1024f);
    var simple = size <= 32;

    // Background tile
    var tile = new SKRect(0, 0, 1024, 1024);
    using (var bg = new SKPaint { IsAntialias = true })
    {
        bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1024, 1024),
            [new SKColor(0x3A, 0x22, 0x7A), new SKColor(0x14, 0x0C, 0x33)], SKShaderTileMode.Clamp);
        c.DrawRoundRect(tile, 230, 230, bg);
    }

    // Frames fanned behind, pivoting on the bottom like the pages of a flip book
    var card = simple ? new SKRect(262, 170, 762, 870) : new SKRect(378, 214, 798, 820);
    var pivot = new SKPoint(card.MidX, card.Bottom + 40);
    if (!simple)
    {
        foreach (var (angle, alpha) in new[] { (-24f, (byte)46), (-12f, (byte)84) })
        {
            using var frame = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, alpha) };
            c.Save();
            c.RotateDegrees(angle, pivot.X, pivot.Y);
            c.DrawRoundRect(card, 56, 56, frame);
            c.Restore();
        }
    }

    // Front frame, warm like a lit screen, with a soft shadow
    using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 90) })
    {
        if (!simple)
            shadow.ImageFilter = SKImageFilter.CreateBlur(28, 28);
        var s = card;
        s.Offset(simple ? 0 : 10, simple ? 0 : 22);
        if (!simple)
            c.DrawRoundRect(s, 56, 56, shadow);
    }
    using (var front = new SKPaint { IsAntialias = true })
    {
        front.Shader = SKShader.CreateLinearGradient(new SKPoint(card.Left, card.Top), new SKPoint(card.Right, card.Bottom),
            [new SKColor(0xFF, 0xC2, 0x4B), new SKColor(0xFF, 0x6A, 0x5C), new SKColor(0xE8, 0x3E, 0x8C)],
            [0f, 0.55f, 1f], SKShaderTileMode.Clamp);
        c.DrawRoundRect(card, simple ? 70 : 56, simple ? 70 : 56, front);
    }

    // Play mark, rounded
    var h = card.Height * (simple ? 0.46f : 0.40f);
    var w = h * 0.88f;
    var cx = card.MidX + w * 0.10f;
    var cy = card.MidY;
    using (var path = new SKPath())
    {
        path.MoveTo(cx - w / 2, cy - h / 2);
        path.LineTo(cx + w / 2, cy);
        path.LineTo(cx - w / 2, cy + h / 2);
        path.Close();
        using var corner = SKPathEffect.CreateCorner(simple ? 40 : 34);
        using var play = new SKPaint { IsAntialias = true, Color = SKColors.White, PathEffect = corner };
        c.DrawPath(path, play);
    }

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// A macOS .icns whose entries are PNGs (the types macOS 10.7+ reads that way).
static void WriteIcns(string path, Dictionary<int, byte[]> pngs)
{
    (string Type, int Size)[] entries =
    [
        ("icp4", 16), ("icp5", 32), ("ic11", 32), ("ic12", 64), ("ic07", 128), ("ic13", 256),
        ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024),
    ];
    using var body = new MemoryStream();
    foreach (var (type, size) in entries)
    {
        var png = pngs[size];
        body.Write(System.Text.Encoding.ASCII.GetBytes(type));
        body.Write(BigEndian(png.Length + 8));
        body.Write(png);
    }
    using var file = File.Create(path);
    file.Write("icns"u8);
    file.Write(BigEndian((int)body.Length + 8));
    body.Position = 0;
    body.CopyTo(file);

    static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}

// An .ico whose entries are PNGs: understood by Windows Vista and later.
static void WriteIco(string path, List<(int Size, byte[] Png)> entries)
{
    using var stream = File.Create(path);
    using var w = new BinaryWriter(stream);
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)entries.Count);
    var offset = 6 + 16 * entries.Count;
    foreach (var (size, png) in entries)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in entries)
        w.Write(png);
}
