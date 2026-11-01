using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

/// <summary>Dibuja el ícono de PC Doctor (cuadrado azul con cruz médica blanca) y arma un .ico con varios tamaños.</summary>
internal static class IconMaker
{
    private static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256];

    public static void Write(string icoPath)
    {
        var pngs = Sizes.Select(Render).ToList();
        File.WriteAllBytes(Path.ChangeExtension(icoPath, ".png"), Render(512));
        using var fs = File.Create(icoPath);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);           // reservado
        w.Write((short)1);           // tipo: ícono
        w.Write((short)pngs.Count);
        var offset = 6 + 16 * pngs.Count;
        for (var i = 0; i < pngs.Count; i++)
        {
            var s = Sizes[i];
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0);        // paleta
            w.Write((byte)0);        // reservado
            w.Write((short)1);       // planos
            w.Write((short)32);      // bits por píxel
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
    }

    private static byte[] Render(int size)
    {
        using var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext())
        {
            var s = size / 256.0;
            var background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#1E6FE0"), 0), new GradientStop(Color.Parse("#0B3D91"), 1) },
            };
            ctx.DrawRectangle(background, null, new RoundedRect(new Rect(8 * s, 8 * s, 240 * s, 240 * s), 52 * s));
            // Cruz médica blanca, con esquinas redondeadas.
            var arm = 56 * s;
            var len = 168 * s;
            var c = 128 * s;
            ctx.DrawRectangle(Brushes.White, null, new RoundedRect(new Rect(c - arm / 2, c - len / 2, arm, len), 12 * s));
            ctx.DrawRectangle(Brushes.White, null, new RoundedRect(new Rect(c - len / 2, c - arm / 2, len, arm), 12 * s));
            // Pequeño pulso verde en la esquina: «la PC está sana».
            if (size >= 32)
                ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#2EBD59")), new Pen(Brushes.White, 10 * s), new Point(198 * s, 198 * s), 34 * s, 34 * s);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms);
        return ms.ToArray();
    }
}
