using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DpiShell.App;

/// <summary>Иконка в трее с меню. Закрытие окна прячет его сюда.</summary>
public sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly MainViewModel _vm;
    readonly Forms.ToolStripMenuItem _enable, _disable, _select, _hostsRevert, _open, _exit;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public Tray(MainViewModel vm)
    {
        _vm = vm;
        var menu = new Forms.ContextMenuStrip();
        _enable = new("", null, (_, _) => Run(_vm.EnableCmd));
        _disable = new("", null, (_, _) => Run(_vm.DisableCmd));
        _select = new("", null, (_, _) => { OpenRequested?.Invoke(); _vm.Page = Pages.Select; });
        // «Вернуть как было» для hosts — всегда под рукой, пока наш блок стоит (architecture.md «Починить адреса»).
        _hostsRevert = new("", null, (_, _) => Run(_vm.Hosts.RevertCmd)) { Visible = false };
        _open = new Forms.ToolStripMenuItem("", null, (_, _) => OpenRequested?.Invoke()) { Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold) };
        _exit = new Forms.ToolStripMenuItem("", null, (_, _) => ExitRequested?.Invoke());
        menu.Items.AddRange([_enable, _disable, _select, _hostsRevert, new Forms.ToolStripSeparator(), _open, _exit]);
        ApplyLang();
        menu.Opening += (_, _) =>
        {
            _enable.Enabled = _vm.EnableCmd.CanExecute(null) && !_vm.IsRunning;
            _disable.Enabled = _vm.DisableCmd.CanExecute(null) && _vm.IsRunning;
            _hostsRevert.Visible = _vm.Hosts.BlockPresent;
        };

        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(); };
        vm.StatusChanged += Update;
        L.Changed += OnLangChanged;
        Update();
    }

    /// <summary>Подписи пунктов меню трея на текущем языке.</summary>
    void ApplyLang()
    {
        _enable.Text = L.T("shell.tray.enable");
        _disable.Text = L.T("shell.tray.disable");
        _select.Text = L.T("shell.tray.select");
        _hostsRevert.Text = L.T("shell.tray.hosts_revert");
        _open.Text = L.T("shell.tray.open");
        _exit.Text = L.T("shell.tray.exit");
    }

    void OnLangChanged() { ApplyLang(); Update(); }

    static void Run(System.Windows.Input.ICommand c) { if (c.CanExecute(null)) c.Execute(null); }

    void Update()
    {
        _icon.Icon = Icons.For(_vm.Kind);
        var text = string.IsNullOrEmpty(_vm.StatusDetail)
            ? L.T("shell.tray.tip", new { status = _vm.StatusTitle })
            : L.T("shell.tray.tip_detail", new { status = _vm.StatusTitle, detail = _vm.StatusDetail });
        _icon.Text = text.Length > 127 ? text[..126] + "…" : text;
    }

    public void Hint(string text) => _icon.ShowBalloonTip(4000, "DpiShell", text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _vm.StatusChanged -= Update;
        L.Changed -= OnLangChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
