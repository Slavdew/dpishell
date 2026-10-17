using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DpiShell.App;

/// <summary>Иконки рисуются программно: кружок цвета состояния. Кэшируются, чтобы не плодить HICON.</summary>
public static class Icons
{
    static readonly Dictionary<StatusKind, Drawing.Icon> Cache = [];

    public static Drawing.Icon For(StatusKind k)
    {
        if (Cache.TryGetValue(k, out var ic)) return ic;
        var c = k switch
        {
            StatusKind.On => Drawing.Color.FromArgb(0x16, 0xA3, 0x4A),
            StatusKind.Busy => Drawing.Color.FromArgb(0xD9, 0x77, 0x06),
            StatusKind.Error => Drawing.Color.FromArgb(0xDC, 0x26, 0x26),
            _ => Drawing.Color.FromArgb(0x6B, 0x72, 0x80),
        };
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var fill = new Drawing.SolidBrush(c);
            g.FillEllipse(fill, 1, 1, 30, 30);
            // Белая «стрелка вперёд» — обход.
            using var pen = new Drawing.Pen(Drawing.Color.White, 4) { StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round, LineJoin = Drawing.Drawing2D.LineJoin.Round };
            g.DrawLines(pen, [new(13, 9), new(20, 16), new(13, 23)]);
        }
        var h = bmp.GetHicon();
        ic = (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        Cache[k] = ic;
        return ic;
    }

    public static BitmapSource ImageFor(StatusKind k)
    {
        var src = Imaging.CreateBitmapSourceFromHIcon(For(k).Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}

/// <summary>Иконка в трее с меню. Закрытие окна прячет его сюда.</summary>
public sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly MainViewModel _vm;
    readonly Forms.ToolStripMenuItem _enable, _disable, _select, _hostsRevert;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public Tray(MainViewModel vm)
    {
        _vm = vm;
        var menu = new Forms.ContextMenuStrip();
        _enable = new("Включить", null, (_, _) => Run(_vm.EnableCmd));
        _disable = new("Выключить", null, (_, _) => Run(_vm.DisableCmd));
        _select = new("Подобрать", null, (_, _) => { OpenRequested?.Invoke(); _vm.Page = Pages.Select; });
        // «Вернуть как было» для hosts — всегда под рукой, пока наш блок стоит (architecture.md «Починить адреса»).
        _hostsRevert = new("Убрать адреса из hosts", null, (_, _) => Run(_vm.Hosts.RevertCmd)) { Visible = false };
        var open = new Forms.ToolStripMenuItem("Открыть", null, (_, _) => OpenRequested?.Invoke()) { Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold) };
        var exit = new Forms.ToolStripMenuItem("Выход", null, (_, _) => ExitRequested?.Invoke());
        menu.Items.AddRange([_enable, _disable, _select, _hostsRevert, new Forms.ToolStripSeparator(), open, exit]);
        menu.Opening += (_, _) =>
        {
            _enable.Enabled = _vm.EnableCmd.CanExecute(null) && !_vm.IsRunning;
            _disable.Enabled = _vm.DisableCmd.CanExecute(null) && _vm.IsRunning;
            _hostsRevert.Visible = _vm.Hosts.BlockPresent;
        };

        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(); };
        vm.StatusChanged += Update;
        Update();
    }

    static void Run(System.Windows.Input.ICommand c) { if (c.CanExecute(null)) c.Execute(null); }

    void Update()
    {
        _icon.Icon = Icons.For(_vm.Kind);
        var text = string.IsNullOrEmpty(_vm.StatusDetail) ? _vm.StatusTitle : $"{_vm.StatusTitle} — {_vm.StatusDetail}";
        text = "DpiShell: " + text;
        _icon.Text = text.Length > 127 ? text[..126] + "…" : text;
    }

    public void Hint(string text) => _icon.ShowBalloonTip(4000, "DpiShell", text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _vm.StatusChanged -= Update;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
