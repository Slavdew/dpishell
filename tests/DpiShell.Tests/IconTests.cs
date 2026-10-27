using System.IO;
using DpiShell.App;
using Drawing = System.Drawing;

namespace DpiShell.Tests;

/// <summary>Значок P3 «Пакет»: размеры, метки состояний, app.ico. PNG трея — при DPISHELL_SNAPSHOTS (папка icon-p3-render).</summary>
public class IconTests
{
    static readonly StatusKind[] Kinds = [StatusKind.Off, StatusKind.On, StatusKind.Busy, StatusKind.Error];

    [Theory]
    [InlineData(16)] [InlineData(20)] [InlineData(24)] [InlineData(32)]
    public void For_returns_icon_of_requested_size(int size)
    {
        foreach (var k in Kinds)
        {
            var ic = Icons.For(k, size);
            Assert.Equal(size, ic.Width);
            Assert.Equal(size, ic.Height);
            Assert.Same(ic, Icons.For(k, size)); // кэш
        }
        Assert.InRange(Icons.For(StatusKind.On).Width, 16, 64);
    }

    static int CountNear(Drawing.Bitmap b, Drawing.Color c, int x0, int y0, int x1, int y1)
    {
        var n = 0;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var p = b.GetPixel(x, y);
                if (p.A > 240 && Math.Abs(p.R - c.R) < 6 && Math.Abs(p.G - c.G) < 6 && Math.Abs(p.B - c.B) < 6) n++;
            }
        return n;
    }

    [Theory]
    [InlineData(16)] [InlineData(20)] [InlineData(24)] [InlineData(32)] [InlineData(48)]
    public void Badge_zone_has_state_color_off_has_pause(int size)
    {
        var half = size / 2;
        foreach (var k in Kinds)
        {
            using var b = Icons.Render(k, size);
            foreach (var other in Kinds)
            {
                var n = CountNear(b, Icons.BadgeColor(other)!.Value, half, half, size, size);
                if (other == k) Assert.True(n >= size * size / 24, $"{k} {size}: метки нет ({n})");
                else Assert.Equal(0, n);
            }
        }
        // Выключено: знак серый, синего нет, метка «пауза» серая (не синяя, не цвета других состояний).
        using var off = Icons.Render(StatusKind.Off, size);
        Assert.Equal(0, CountNear(off, Icons.Blue, 0, 0, size, size));
        Assert.True(CountNear(off, Icons.Gray, 0, 0, size, size) > 0);
        // Работает: знак синий.
        using var on = Icons.Render(StatusKind.On, size);
        Assert.True(CountNear(on, Icons.Blue, 0, 0, half, size) > 0);
    }

    [Fact]
    public void Off_badge_contrast_on_both_panels()
    {
        static double Lum(Drawing.Color c)
        {
            static double F(int v) { var x = v / 255.0; return x <= .03928 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
            return .2126 * F(c.R) + .7152 * F(c.G) + .0722 * F(c.B);
        }
        static double Cr(Drawing.Color a, Drawing.Color b) { var (x, y) = (Lum(a), Lum(b)); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05); }
        var m = Icons.BadgeColor(StatusKind.Off)!.Value;
        Assert.True(Cr(m, Drawing.Color.FromArgb(0x20, 0x20, 0x20)) >= 3);
        Assert.True(Cr(m, Drawing.Color.FromArgb(0xf3, 0xf3, 0xf3)) >= 3);
        Assert.True(Cr(m, Drawing.Color.White) >= 4.5);
    }

    [Fact]
    public void Ico_has_all_sizes()
    {
        var ico = File.ReadAllBytes(IcoPath());
        Assert.Equal(1, BitConverter.ToInt16(ico, 2));
        var count = BitConverter.ToInt16(ico, 4);
        var sizes = Enumerable.Range(0, count).Select(i => ico[6 + 16 * i] is 0 ? 256 : ico[6 + 16 * i]).ToArray();
        Assert.Equal(Icons.IcoSizes, sizes);
        using var ic = new Drawing.Icon(new MemoryStream(ico), 48, 48);
        Assert.Equal(48, ic.Width);
    }

    [Fact]
    public void Ico_matches_generator()
    {
        var fresh = Icons.BuildIco(Icons.IcoSizes);
        var write = Environment.GetEnvironmentVariable("DPISHELL_WRITE_ICO");
        if (!string.IsNullOrEmpty(write)) File.WriteAllBytes(write, fresh);
        else
        {
            var cur = File.ReadAllBytes(IcoPath());
            var diff = Enumerable.Range(0, Math.Min(fresh.Length, cur.Length)).Where(i => fresh[i] != cur[i]).ToArray();
            // PNG-запись 256 px может сжаться иначе, поэтому длина не обязана совпасть; пиксели малых размеров — почти побайтно.
            var small = Enumerable.Range(0, Math.Min(fresh.Length, cur.Length)).TakeWhile(i => i < 6 + 16 * Icons.IcoSizes.Length + 20000).Count(i => fresh[i] != cur[i]);
            Assert.True(small < 40,$"app.ico устарел (tools\\make-icon.ps1): длины {fresh.Length}/{cur.Length}, отличий {diff.Length}, первое {diff.FirstOrDefault()}");
        }
    }

    static string IcoPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "DpiShell.sln"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "src", "DpiShell.App", "Assets", "app.ico");
    }

    [Fact]
    public void Tray_png_snapshots()
    {
        var snap = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        if (snap is null) return;
        var dir = Path.Combine(snap, "icon-p3-render");
        Directory.CreateDirectory(dir);
        int[] sizes = [16, 20, 24, 32];
        const int zoom = 6, pad = 8;
        foreach (var dark in new[] { false, true })
        {
            var bg = dark ? Drawing.Color.FromArgb(0x20, 0x20, 0x20) : Drawing.Color.FromArgb(0xf3, 0xf3, 0xf3);
            var name = dark ? "dark" : "light";
            using var sheet = new Drawing.Bitmap(Kinds.Length * (32 * zoom + pad) + pad, sizes.Length * (32 * zoom + pad) + pad);
            using var g = Drawing.Graphics.FromImage(sheet);
            g.Clear(bg);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            for (var si = 0; si < sizes.Length; si++)
                for (var ki = 0; ki < Kinds.Length; ki++)
                {
                    using var icon = Icons.Render(Kinds[ki], sizes[si]);
                    using var flat = new Drawing.Bitmap(sizes[si], sizes[si]);
                    using (var fg = Drawing.Graphics.FromImage(flat)) { fg.Clear(bg); fg.DrawImage(icon, 0, 0, sizes[si], sizes[si]); }
                    flat.Save(Path.Combine(dir, $"{Kinds[ki]}-{sizes[si]}-{name}.png"));
                    g.DrawImage(flat, pad + ki * (32 * zoom + pad), pad + si * (32 * zoom + pad), sizes[si] * zoom, sizes[si] * zoom);
                }
            sheet.Save(Path.Combine(dir, $"sheet-{name}.png"));
        }
    }
}
