using System.IO;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// «Настройки»: выгрузка и загрузка своих настроек (<see cref="TransferViewModel"/>, docs/settings-export.md) и выбор адресов для проверок
/// («Через защищённый DNS» / «Как у системы», п. 17 замечаний владельца).
/// </summary>
public sealed partial class MainViewModel
{
    // ---------- адреса сайтов для проверок ----------

    /// <summary>Что уходит помощнику и Shell: <see cref="CheckDns.Secure"/> или <see cref="CheckDns.System"/>.</summary>
    public string CheckDnsMode => CheckDns.Normalize(Settings.CheckDns);

    public bool CheckDnsSecure { get => !CheckDnsSystem; set { if (value) SetCheckDnsMode(CheckDns.Secure); } }
    public bool CheckDnsSystem { get => CheckDns.IsSystem(Settings.CheckDns); set { if (value) SetCheckDnsMode(CheckDns.System); } }

    void SetCheckDnsMode(string mode)
    {
        if (CheckDnsMode == mode) return;
        Settings.CheckDns = mode;
        _shell.CheckDnsMode = mode;
        SaveSettings();
        Raise(nameof(CheckDnsSecure)); Raise(nameof(CheckDnsSystem)); Raise(nameof(CheckDnsMode));
        // Сводка на «Сети» собрана под прежний режим — пересчитать (в системном режиме сравнения с DoH нет).
        if (!IsLoading && ProbeGroups is null) _ = CheckDnsAsync();
    }

    /// <summary>
    /// Пометка в итоге проверки, если адреса взяты не через защищённый DNS: по выбору («как у системы») или потому, что он не ответил.
    /// <paramref name="fragment"/> — кусок списка пояснений (со строчной), иначе — предложение. null — всё как обычно.
    /// </summary>
    static string? AddressSourceNote(string? source, bool fragment, bool onlyFallback = false) => source switch
    {
        CheckDns.SourceSystem when !onlyFallback => L.T(fragment ? "settings.checkdns.frag_system" : "settings.checkdns.note_system"),
        CheckDns.SourceFallback => L.T(fragment ? "settings.checkdns.frag_fallback" : "settings.checkdns.note_fallback"),
        _ => null,
    };

    // ---------- выгрузка и загрузка настроек ----------

    /// <summary>Выбор папки (заголовок, прежний путь) → путь или null. В тестах подменяется — настоящих диалогов под тестом нет.</summary>
    public Func<string, string?, string?> ChooseFolder { get; set; } = (title, initial) =>
    {
        UiGuard.Check(title);
        var d = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (initial is { Length: > 0 } && Directory.Exists(initial)) d.InitialDirectory = initial;
        return d.ShowDialog() == true ? d.FolderName : null;
    };

    /// <summary>Выбор файла архива для записи (заголовок, фильтр, предлагаемое имя) → путь или null.</summary>
    public Func<string, string, string, string?> ChooseSaveZip { get; set; } = (title, filter, name) =>
    {
        UiGuard.Check(title);
        var d = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = name, DefaultExt = ".zip", AddExtension = true, OverwritePrompt = true };
        return d.ShowDialog() == true ? d.FileName : null;
    };

    /// <summary>Выбор файла архива для чтения (заголовок, фильтр) → путь или null.</summary>
    public Func<string, string, string?> ChooseOpenZip { get; set; } = (title, filter) =>
    {
        UiGuard.Check(title);
        var d = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return d.ShowDialog() == true ? d.FileName : null;
    };

    /// <summary>Показать окно выгрузки/загрузки (в тестах подменяется — окно не показывается).</summary>
    public Action<TransferViewModel> ShowTransfer { get; set; } = TransferView.ShowDialog;

    /// <summary>Папка по умолчанию для выгрузки (в тестах — временная).</summary>
    public Func<string> TransferFolder { get; set; } = () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public ICommand ExportSettingsCmd => _exportSettingsCmd ??= new RelayCommand(() => OpenTransfer(import: false), () => !Busy && !IsLoading);
    public ICommand ImportSettingsCmd => _importSettingsCmd ??= new RelayCommand(() => OpenTransfer(import: true), () => !Busy && !IsLoading);
    ICommand? _exportSettingsCmd, _importSettingsCmd;

    /// <summary>Окно выгрузки/загрузки (для теста и снимка).</summary>
    public TransferViewModel CreateTransfer(bool import)
    {
        var t = new TransferViewModel
        {
            Need = NeedHelperAsync,
            HasHelper = () => !NeedsHelper,
            ChooseFolder = (title, initial) => ChooseFolder(title, initial),
            ChooseSaveZip = (title, filter, name) => ChooseSaveZip(title, filter, name),
            ChooseOpenZip = (title, filter) => ChooseOpenZip(title, filter),
            OpenFolder = p => OpenFolderAction(p),
            DefaultFolder = () => TransferFolder(),
            AfterImport = AfterImportAsync,
            Say = s => Message = s,
            Confirm = s => Confirm(s),
        };
        t.Start(import);
        return t;
    }

    void OpenTransfer(bool import) => ShowTransfer(CreateTransfer(import));

    /// <summary>После загрузки настроек: всё, что окно показывает из данных помощника, перечитать.</summary>
    async Task AfterImportAsync()
    {
        await FetchAsync(full: true);
        _targetsFromHelper = false;
        _excludeFromHelper = false;
        ApplyHelperData();
        await RefreshStatusAsync();
        await RefreshPcDnsAsync();
        await Hosts.RefreshAsync();
        await Apps.RefreshAsync();
        await Changes.RefreshAsync();
    }
}
