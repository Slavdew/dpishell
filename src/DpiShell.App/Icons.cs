using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using D2D = System.Drawing.Drawing2D;

namespace DpiShell.App;

/// <summary>
/// Значок DpiShell: изометрический короб-«пакет» (вариант P3, исходник — docs/img/icon.svg) и метка состояния в правом нижнем углу.
/// Рисуется программно; тот же код делает Assets/app.ico (tools/make-icon.ps1), поэтому значки не расходятся.
/// Выключено — серый знак без метки; работает, подбор, ошибка — метка с галочкой, часами, «!».
/// Для ≤20 px глифы метки рисуются целыми пикселями, чтобы читались.
/// </summary>
public static class Icons
{
    static readonly Dictionary<(StatusKind, int), Drawing.Icon> Cache = [];

    public static readonly Drawing.Color Blue = Drawing.Color.FromArgb(0x25, 0x63, 0xEB);
    public static readonly Drawing.Color Gray = Drawing.Color.FromArgb(0x9C, 0xA3, 0xAF);
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    /// <summary>Цвет метки состояния; у «выключено» метки нет (null).</summary>
    public static Drawing.Color? BadgeColor(StatusKind k) => k switch
    {
        StatusKind.On => Drawing.Color.FromArgb(0x16, 0xA3, 0x4A),
        StatusKind.Busy => Drawing.Color.FromArgb(0xD9, 0x77, 0x06),
        StatusKind.Error => Drawing.Color.FromArgb(0xDC, 0x26, 0x26),
        _ => null,
    };

    /// <summary>Размер значка в трее по настройкам системы (16/20/24/32 в зависимости от DPI).</summary>
    public static int TraySize()
    {
        var s = GetSystemMetrics(49 /* SM_CXSMICON */);
        return s is >= 16 and <= 64 ? s : 16;
    }

    /// <summary>Иконка трея (size = 0 — по системной метрике). Кэшируется.</summary>
    public static Drawing.Icon For(StatusKind k, int size = 0)
    {
        if (size <= 0) size = TraySize();
        if (Cache.TryGetValue((k, size), out var ic)) return ic;
        using var bmp = Render(k, size);
        var h = bmp.GetHicon();
        ic = (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        Cache[(k, size)] = ic;
        return ic;
    }

    /// <summary>Значок окна: 32 px.</summary>
    public static BitmapSource ImageFor(StatusKind k, int size = 32)
    {
        using var bmp = Render(k, size);
        var h = bmp.GetHbitmap(Drawing.Color.Transparent);
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(h); }
    }

    /// <summary>Рисует значок состояния k; withBadge = false — только знак (для .ico), синий.</summary>
    public static Drawing.Bitmap Render(StatusKind k, int size, bool withBadge = true)
    {
        var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = D2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = D2D.PixelOffsetMode.HighQuality;
        g.Clear(Drawing.Color.Transparent);
        DrawMark(g, size, k == StatusKind.Off && withBadge ? Gray : Blue);
        if (withBadge && BadgeColor(k) is { } c) DrawBadge(g, size, k, c);
        return bmp;
    }

    static Drawing.PointF[] Pts(float s, params float[] xy)
    {
        var r = new Drawing.PointF[xy.Length / 2];
        for (var i = 0; i < r.Length; i++) r[i] = new(xy[2 * i] * s, xy[2 * i + 1] * s);
        return r;
    }

    static void DrawMark(Drawing.Graphics g, int size, Drawing.Color body)
    {
        var s = size / 64f;
        using (var b = new Drawing.SolidBrush(body)) g.FillPolygon(b, Pts(s, 32, 6, 56, 18, 56, 46, 32, 58, 8, 46, 8, 18));
        using (var b = new Drawing.SolidBrush(Drawing.Color.FromArgb(71, 255, 255, 255))) g.FillPolygon(b, Pts(s, 8, 18, 32, 30, 56, 18, 32, 6));
        using (var b = new Drawing.SolidBrush(Drawing.Color.FromArgb(46, 0, 0, 0))) g.FillPolygon(b, Pts(s, 32, 30, 56, 18, 56, 46, 32, 58));
        // Лента. На малых размерах концы по центрам пикселей и толщина не меньше 1.5 px, иначе линия тает.
        var small = size <= 20;
        var pts = Pts(s, 20, 12, 44, 24, 44, 33);
        if (small) for (var i = 0; i < pts.Length; i++) pts[i] = new(MathF.Floor(pts[i].X) + .5f, MathF.Floor(pts[i].Y) + .5f);
        using var pen = new Drawing.Pen(Drawing.Color.White, small ? Math.Max(1.5f, 4 * s) : 4 * s)
        { StartCap = D2D.LineCap.Round, EndCap = D2D.LineCap.Round, LineJoin = D2D.LineJoin.Round };
        g.DrawLines(pen, pts);
    }

    static void DrawBadge(Drawing.Graphics g, int size, StatusKind k, Drawing.Color c)
    {
        if (size <= 20) { DrawBadgeSmall(g, size, k, c); return; }
        var d = size * 0.56f;
        var x0 = size - d; var y0 = size - d;
        var b = d / 24f;
        // Кольцо вокруг метки — вырез в прозрачность, чтобы значок не зависел от цвета панели.
        var old = g.CompositingMode;
        g.CompositingMode = D2D.CompositingMode.SourceCopy;
        using (var clear = new Drawing.SolidBrush(Drawing.Color.Transparent)) g.FillEllipse(clear, x0, y0, d, d);
        g.CompositingMode = old;
        using (var fill = new Drawing.SolidBrush(c)) g.FillEllipse(fill, x0 + 2.25f * b, y0 + 2.25f * b, 19.5f * b, 19.5f * b);
        Drawing.PointF P(float x, float y) => new(x0 + x * b, y0 + y * b);
        using var pen = new Drawing.Pen(Drawing.Color.White) { StartCap = D2D.LineCap.Round, EndCap = D2D.LineCap.Round, LineJoin = D2D.LineJoin.Round };
        using var white = new Drawing.SolidBrush(Drawing.Color.White);
        switch (k)
        {
            case StatusKind.On:
                pen.Width = 3 * b; g.DrawLines(pen, [P(7, 12.5f), P(10.5f, 16), P(17, 8.5f)]); break;
            case StatusKind.Busy:
                pen.Width = 2.8f * b; g.DrawLines(pen, [P(12, 6.5f), P(12, 12), P(15.5f, 14)]); break;
            default:
                pen.Width = 3.2f * b; g.DrawLine(pen, P(12, 6.5f), P(12, 13));
                g.FillEllipse(white, x0 + (12 - 1.9f) * b, y0 + (17.2f - 1.9f) * b, 3.8f * b, 3.8f * b); break;
        }
    }

    // Малые размеры: метка d = round(0.56·size) px (16 → 9, 20 → 11), кольцо 1 px, глиф 7×7 целыми пикселями.
    static void DrawBadgeSmall(Drawing.Graphics g, int size, StatusKind k, Drawing.Color c)
    {
        var d = (int)MathF.Round(size * 0.56f);
        var x0 = size - d; var y0 = size - d;
        var old = g.CompositingMode;
        g.CompositingMode = D2D.CompositingMode.SourceCopy;
        using (var clear = new Drawing.SolidBrush(Drawing.Color.Transparent)) g.FillEllipse(clear, x0, y0, d, d);
        g.CompositingMode = old;
        using (var fill = new Drawing.SolidBrush(c)) g.FillEllipse(fill, x0 + 1, y0 + 1, d - 2, d - 2);
        var gx = x0 + 1 + (d - 2 - 7) / 2; var gy = y0 + 1 + (d - 2 - 7) / 2;
        (int x, int y)[] px = k switch
        {
            StatusKind.On => [(1, 3), (2, 4), (3, 3), (4, 2), (5, 1)],
            StatusKind.Busy => [(3, 1), (3, 2), (3, 3), (4, 4), (5, 5)],
            _ => [(3, 1), (3, 2), (3, 3), (3, 5)],
        };
        using var white = new Drawing.SolidBrush(Drawing.Color.White);
        g.SmoothingMode = D2D.SmoothingMode.None;
        foreach (var (x, y) in px) g.FillRectangle(white, gx + x, gy + y, 1, 1);
        g.SmoothingMode = D2D.SmoothingMode.AntiAlias;
    }

    /// <summary>Содержимое .ico: DIB-записи для малых размеров, PNG для 256. Без метки, синий знак.</summary>
    public static byte[] BuildIco(int[] sizes)
    {
        var images = new List<byte[]>();
        foreach (var sz in sizes)
        {
            using var bmp = Render(StatusKind.On, sz, withBadge: false);
            using var m = new MemoryStream();
            if (sz >= 256) bmp.Save(m, Drawing.Imaging.ImageFormat.Png);
            else
            {
                using var w = new BinaryWriter(m, System.Text.Encoding.UTF8, leaveOpen: true);
                w.Write(40); w.Write(sz); w.Write(sz * 2); w.Write((short)1); w.Write((short)32);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (var y = sz - 1; y >= 0; y--)
                    for (var x = 0; x < sz; x++) w.Write(bmp.GetPixel(x, y).ToArgb());
                w.Write(new byte[(sz + 31) / 32 * 4 * sz]); // маска AND нулевая: прозрачность — из альфа-канала
            }
            images.Add(m.ToArray());
        }
        using var o = new MemoryStream();
        using var bw = new BinaryWriter(o);
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var b = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            bw.Write(b); bw.Write(b); bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32);
            bw.Write(images[i].Length); bw.Write(offset);
            offset += images[i].Length;
        }
        foreach (var im in images) bw.Write(im);
        bw.Flush();
        return o.ToArray();
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
}
