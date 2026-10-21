using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Export;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>Строка раздела в окне выгрузки/загрузки: галочка, название, число записей или итог плана, предупреждение и раскрываемый список записей.</summary>
public sealed class TransferSection : Observable
{
    readonly Func<string> _count;
    readonly Func<string>? _warn;
    readonly Func<IReadOnlyList<string>>? _notes;
    readonly Func<string>? _title;

    public TransferSection(string id, Func<string> count, bool risky = false, Func<string>? warn = null, IReadOnlyList<string>? items = null,
        bool enabled = true, Func<IReadOnlyList<string>>? notes = null, Func<string>? title = null, bool linesLabel = false)
    {
        Id = id; _count = count; Risky = risky; _warn = warn; Items = items ?? []; Enabled = enabled; _notes = notes; _title = title; LinesLabel = linesLabel;
    }

    public string Id { get; }
    public string Title => _title?.Invoke() ?? SettingsSections.Title(Id);
    /// <summary>«12», «4 файла», «+9 · 3 уже есть», «пропущен» — собирается при показе, на языке окна.</summary>
    public string CountText => _count();
    public bool Risky { get; }
    /// <summary>Раздел можно отметить (есть что выгружать/загружать и он не пропущен).</summary>
    public bool Enabled { get; }
    public string Warn => _warn?.Invoke() ?? "";
    public bool HasWarn => Warn.Length > 0;
    public IReadOnlyList<string> Items { get; }
    public bool HasItems => Items.Count > 0;
    /// <summary>Пояснения под строкой: что пропущено и почему.</summary>
    public IReadOnlyList<string> Notes => _notes?.Invoke() ?? [];
    public bool HasNotes => Notes.Count > 0;
    readonly bool LinesLabel;

    bool _checked;
    public bool IsChecked { get => _checked; set { if (Enabled && Set(ref _checked, value)) Changed?.Invoke(); } }

    bool _showItems;
    public bool ShowItems { get => _showItems; set { if (Set(ref _showItems, value)) Raise(nameof(ToggleText)); } }
    /// <summary>«Показать» / «Скрыть»; у hosts при загрузке — «Показать строки».</summary>
    public string ToggleText => L.T(ShowItems ? "settings.transfer.hide" : LinesLabel ? "settings.transfer.show_lines" : "settings.transfer.show");
    public ICommand ToggleCmd => _toggle ??= new RelayCommand(() => ShowItems = !ShowItems);
    ICommand? _toggle;

    /// <summary>Записи для показа: не больше 200, остальное — «…и ещё N».</summary>
    public string ItemsText
    {
        get
        {
            var shown = Items.Take(200);
            var text = string.Join(Environment.NewLine, shown);
            return Items.Count > 200 ? text + Environment.NewLine + L.T("settings.transfer.more", new { n = Items.Count - 200 }) : text;
        }
    }

    public event Action? Changed;
}

/// <summary>
/// Окно «Выгрузить / Загрузить настройки» (п. 14 замечаний владельца, docs/settings-export.md). Свои данные читает и пишет только помощник
/// (<see cref="HelperClient.SettingsExportReadAsync"/>, <see cref="HelperClient.SettingsImportAsync"/>); пакет — папка или .zip — читается самим окном
/// с теми же проверками. Диалоги выбора папки и файла — подменяемые функции (в тестах настоящих диалогов нет).
/// </summary>
public sealed class TransferViewModel : Observable
{
    // ---------- связи с окном ----------

    public Func<Task<HelperClient?>> Need { get; set; } = () => Task.FromResult<HelperClient?>(null);
    public Func<bool> HasHelper { get; set; } = () => true;
    public Func<string, string?, string?> ChooseFolder { get; set; } = (_, _) => null;
    public Func<string, string, string, string?> ChooseSaveZip { get; set; } = (_, _, _) => null;
    public Func<string, string, string?> ChooseOpenZip { get; set; } = (_, _) => null;
    public Action<string> OpenFolder { get; set; } = _ => { };
    public Func<string> DefaultFolder { get; set; } = () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    /// <summary>После записи: окно перечитывает данные помощника.</summary>
    public Func<Task> AfterImport { get; set; } = () => Task.CompletedTask;
    public Action<string> Say { get; set; } = _ => { };
    public Action? Close { get; set; }

    /// <summary>Для значка щита (шаблон Shield смотрит DataContext окна): помощника нет — действие спросит права.</summary>
    public bool NeedsHelper => !HasHelper();

    public ICommand CloseCmd => _close ??= new RelayCommand(() => Close?.Invoke());
    ICommand? _close;

    // ---------- вкладки ----------

    bool _import;
    public bool IsImport { get => _import; set { if (Set(ref _import, value)) { Raise(nameof(IsExport)); Raise(nameof(Title)); if (!value) _ = EnsureExportAsync(); } } }
    public bool IsExport { get => !_import; set { if (value) IsImport = false; } }
    public string Title => L.T(IsImport ? "settings.transfer.import_title" : "settings.transfer.export_title");

    bool _busy;
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) { Raise(nameof(CanExport)); Raise(nameof(CanImport)); CommandManager.InvalidateRequerySuggested(); } } }
    string _busyText = "";
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    /// <summary>Начать: вкладка и чтение своих данных (для выгрузки).</summary>
    public void Start(bool import)
    {
        _import = import;
        if (!import) _ = EnsureExportAsync();
    }

    // ---------- выгрузка ----------

    public ObservableCollection<TransferSection> ExportSections { get; } = [];
    SettingsState? _state;
    Task? _exportLoad;

    string _exportError = "";
    public string ExportError { get => _exportError; private set { if (Set(ref _exportError, value)) Raise(nameof(HasExportError)); } }
    public bool HasExportError => ExportError.Length > 0;

    string _provider = "";
    public string Provider { get => _provider; set => Set(ref _provider, value); }
    string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    bool _zip = true;
    /// <summary>Куда: false — папка, true — архив .zip (по умолчанию: один файл можно отправить в мессенджере).</summary>
    public bool ExportZip
    {
        get => _zip;
        set
        {
            if (!Set(ref _zip, value)) return;
            Raise(nameof(ExportFolder));
            if (!_pathChosen) ExportPath = DefaultExportPath();
        }
    }
    public bool ExportFolder { get => !_zip; set { if (value) ExportZip = false; } }

    bool _pathChosen;
    string _exportPath = "";
    public string ExportPath { get => _exportPath; private set { if (Set(ref _exportPath, value)) { Raise(nameof(CanExport)); Raise(nameof(HasExportPath)); } } }
    public bool HasExportPath => ExportPath.Length > 0;

    string DefaultExportPath()
    {
        var name = L.T("settings.transfer.default_name", new { date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        return Path.Combine(DefaultFolder(), name + (ExportZip ? ".zip" : ""));
    }

    bool _showAll;
    public bool ShowAll
    {
        get => _showAll;
        private set { if (Set(ref _showAll, value)) { foreach (var s in ExportSections) s.ShowItems = value && s.IsChecked && s.HasItems; Raise(nameof(ShowAllText)); } }
    }
    public string ShowAllText => L.T(ShowAll ? "settings.transfer.hide_all" : "settings.transfer.show_all");
    public ICommand ShowAllCmd => _showAllCmd ??= new RelayCommand(() => ShowAll = !ShowAll);
    ICommand? _showAllCmd;

    public bool CanExport => !Busy && _state is not null && ExportSections.Any(s => s.IsChecked) && HasExportPath;

    public Task EnsureExportAsync() => _exportLoad ??= LoadExportAsync();

    async Task LoadExportAsync()
    {
        Busy = true; BusyText = L.T("settings.transfer.reading");
        try
        {
            var c = await Need();
            if (c is null) { ExportError = L.T("settings.transfer.helper_missing"); return; }
            if (!c.Compat.Supports("settings_export_read")) { ExportError = L.T("settings.transfer.helper_old"); return; }
            _state = await c.SettingsExportReadAsync();
            ExportError = "";
            BuildExport();
        }
        catch (Exception e) when (e is HelperException or HelperUnavailableException or IOException)
        {
            ExportError = Texts.Friendly(e);
            _exportLoad = null;
        }
        finally { Busy = false; BusyText = ""; }
    }

    void BuildExport()
    {
        if (_state is null) return;
        ExportSections.Clear();
        foreach (var p in SettingsExport.Preview(_state))
        {
            var id = p.Id;
            var count = p.Count;
            Func<string> text = id switch
            {
                SettingsSections.Strategies => () => L.Plural("settings.transfer.files", count),
                SettingsSections.Advanced => () => L.Plural("settings.transfer.rows", count),
                SettingsSections.Dns => () => p.Items.FirstOrDefault() ?? "—",
                _ => () => count.ToString(CultureInfo.CurrentCulture),
            };
            Func<string>? warn = !p.Risky ? null : id == SettingsSections.Hosts ? () => L.T("settings.transfer.warn_hosts_out") : () => L.T("settings.transfer.warn_dns_out");
            var s = new TransferSection(id, text, p.Risky, warn, p.Items, enabled: count > 0)
            {
                // Пустые — без галочки; рискованные (hosts, чужой DNS) — тоже: выгружать только сознательно.
                IsChecked = count > 0 && !p.Risky,
            };
            s.Changed += () => { Raise(nameof(CanExport)); if (ShowAll) s.ShowItems = s.IsChecked && s.HasItems; };
            ExportSections.Add(s);
        }
        if (!_pathChosen) ExportPath = DefaultExportPath();
        Raise(nameof(CanExport));
    }

    public ICommand BrowseExportCmd => _browseExport ??= new RelayCommand(BrowseExport);
    ICommand? _browseExport;

    void BrowseExport()
    {
        string? path;
        var name = Path.GetFileName(ExportPath.TrimEnd('\\', '/'));
        if (ExportZip) path = ChooseSaveZip(L.T("settings.transfer.pick_zip_save"), L.T("settings.transfer.zip_filter"), name);
        else
        {
            var parent = Path.GetDirectoryName(ExportPath.TrimEnd('\\', '/'));
            var dir = ChooseFolder(L.T("settings.transfer.pick_folder_out"), parent);
            // Выбрана папка — выгрузка пойдёт в подпапку с датой, чтобы не смешаться с чужими файлами.
            path = dir is null ? null : Path.Combine(dir, L.T("settings.transfer.default_name", new { date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }));
        }
        if (path is null) return;
        _pathChosen = true;
        ExportPath = path;
    }

    string _exportResult = "";
    public string ExportResultText { get => _exportResult; private set { if (Set(ref _exportResult, value)) Raise(nameof(HasExportResult)); } }
    public bool HasExportResult => ExportResultText.Length > 0;
    string _exportedTo = "";
    public ICommand OpenExportedCmd => _openExported ??= new RelayCommand(() => OpenFolder(ExportZip ? Path.GetDirectoryName(_exportedTo) ?? _exportedTo : _exportedTo), () => _exportedTo.Length > 0);
    ICommand? _openExported;

    public ICommand ExportCmd => _exportCmd ??= RelayCommand.Async(ExportAsync, () => CanExport);
    ICommand? _exportCmd;

    public async Task ExportAsync()
    {
        if (_state is null) return;
        var chosen = ExportSections.Where(s => s.IsChecked).Select(s => s.Id).ToList();
        if (chosen.Count == 0) { ExportResultText = L.T("settings.transfer.nothing_chosen"); return; }
        if (!HasExportPath) { ExportResultText = L.T("settings.transfer.no_path"); return; }
        Busy = true; BusyText = L.T("settings.transfer.export_busy");
        ExportResultText = "";
        try
        {
            var state = _state;
            var meta = new ExportMeta(Provider, Note);
            var path = ExportPath;
            var zip = ExportZip;
            var r = await Task.Run(() => zip ? SettingsExport.WriteZip(path, state, chosen, meta) : SettingsExport.Write(path, state, chosen, meta));
            _exportedTo = r.Folder;
            var text = L.T("settings.transfer.export_done", new { sections = string.Join(", ", r.Sections.Select(SettingsSections.Title)) }) + " " + r.Folder;
            if (zip) text += " " + L.T("settings.transfer.export_zip_hint");
            if (r.Skipped.Count > 0) text += " " + L.T("settings.transfer.export_skipped", new { list = string.Join("; ", r.Skipped.Select(x => x.ToString())) });
            ExportResultText = text;
            Say(L.T("settings.transfer.export_done", new { sections = string.Join(", ", r.Sections.Select(SettingsSections.Title)) }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ExportResultText = L.T("settings.transfer.export_failed", new { error = Texts.Friendly(e) });
        }
        finally { Busy = false; BusyText = ""; CommandManager.InvalidateRequerySuggested(); }
    }

    // ---------- загрузка ----------

    public ObservableCollection<TransferSection> ImportSections { get; } = [];
    ParsedPackage? _pkg;
    string _source = "";

    string _importError = "";
    public string ImportError { get => _importError; private set { if (Set(ref _importError, value)) Raise(nameof(HasImportError)); } }
    public bool HasImportError => ImportError.Length > 0;

    public bool HasPackage => _pkg is { Ok: true };

    /// <summary>«Из «Название» · 18.10.2026 · DpiShell 0.9.3» — собирается из данных пакета при показе.</summary>
    public string FromText
    {
        get
        {
            if (_pkg is not { Manifest: { } m }) return L.T("settings.transfer.import_intro");
            var name = Path.GetFileNameWithoutExtension(_source.TrimEnd('\\', '/'));
            var app = m.AppVersion.StartsWith("router", StringComparison.OrdinalIgnoreCase) ? m.AppVersion : ("DpiShell " + m.AppVersion).Trim();
            if (!DateTime.TryParse(m.Created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                return L.T("settings.transfer.from_short", new { name }) + " · " + app;
            return L.T("settings.transfer.from", new { name, date = L.Date(when.ToLocalTime()), app });
        }
    }
    public string ProviderText => _pkg?.Manifest?.Provider is { Length: > 0 } p ? L.T("settings.transfer.provider", new { provider = p }) : "";
    public bool HasProviderText => ProviderText.Length > 0;
    public string PackageNote => _pkg?.Manifest?.Note ?? "";
    public bool HasPackageNote => PackageNote.Length > 0;

    bool _replace;
    /// <summary>«Как загрузить»: false — «Добавить к своим», true — «Заменить».</summary>
    public bool ReplaceMode { get => _replace; set { if (Set(ref _replace, value)) { Raise(nameof(AddMode)); RebuildImport(); } } }
    public bool AddMode { get => !_replace; set { if (value) ReplaceMode = false; } }

    public ICommand PickFolderCmd => _pickFolder ??= RelayCommand.Async(async () =>
    {
        var path = ChooseFolder(L.T("settings.transfer.pick_folder_in"), null);
        if (path is not null) await LoadPackageAsync(path);
    }, () => !Busy);
    ICommand? _pickFolder;

    public ICommand PickZipCmd => _pickZip ??= RelayCommand.Async(async () =>
    {
        var path = ChooseOpenZip(L.T("settings.transfer.pick_zip_open"), L.T("settings.transfer.zip_filter"));
        if (path is not null) await LoadPackageAsync(path);
    }, () => !Busy);
    ICommand? _pickZip;

    /// <summary>Прочитать пакет (папку или .zip) и построить план по своим данным.</summary>
    public async Task LoadPackageAsync(string path)
    {
        Busy = true; BusyText = L.T("settings.transfer.planning");
        ImportError = ""; ImportResultText = ""; ImportSections.Clear(); _pkg = null; _source = path;
        try
        {
            var c = await Need();
            if (c is null) { ImportError = L.T("settings.transfer.helper_missing"); return; }
            if (!c.Compat.Supports("settings_export_read") || !c.Compat.Supports("settings_import")) { ImportError = L.T("settings.transfer.helper_old"); return; }
            _state = await c.SettingsExportReadAsync();
            var pkg = await Task.Run(() => SettingsImport.ReadPackage(path));
            if (!pkg.Ok) { ImportError = pkg.Fatal!; return; }
            _pkg = pkg;
            RebuildImport();
        }
        catch (Exception e) when (e is HelperException or HelperUnavailableException or IOException or UnauthorizedAccessException)
        {
            ImportError = Texts.Friendly(e);
        }
        finally
        {
            Busy = false; BusyText = "";
            RaiseImportHeader();
        }
    }

    void RaiseImportHeader()
    {
        foreach (var n in new[] { nameof(FromText), nameof(ProviderText), nameof(HasProviderText), nameof(PackageNote), nameof(HasPackageNote), nameof(HasPackage), nameof(CanImport) }) Raise(n);
    }

    static string Summary(SectionPlan sp)
    {
        var parts = new List<string>();
        if (sp.Added > 0) parts.Add(L.T("settings.transfer.sum_add", new { n = sp.Added }));
        if (sp.Removed > 0) parts.Add(L.T("settings.transfer.sum_remove", new { n = sp.Removed }));
        if (sp.Same > 0) parts.Add(L.T("settings.transfer.sum_same", new { n = sp.Same }));
        if (sp.Skipped > 0) parts.Add(L.T("settings.transfer.sum_skip", new { n = sp.Skipped }));
        return parts.Count == 0 ? L.T("settings.transfer.sum_none") : string.Join(" · ", parts);
    }

    /// <summary>Разделы пакета с итогом плана. Отметки пользователя при смене режима сохраняются.</summary>
    void RebuildImport()
    {
        if (_pkg is not { Ok: true } pkg || _state is null) return;
        var was = ImportSections.ToDictionary(s => s.Id, s => s.IsChecked);
        var usable = pkg.Sections.Where(s => s.Usable).Select(s => s.Id).ToList();
        var plan = SettingsImport.Plan(pkg, _state, usable, ReplaceMode ? ImportMode.Replace : ImportMode.Add);
        ImportSections.Clear();
        foreach (var ps in pkg.Sections)
        {
            var id = ps.Id;
            var problems = pkg.ProblemsOf(id).Select(p => p.ToString()).ToList();
            if (!ps.Usable)
            {
                var reason = ps.SkippedReason!;
                var why = new List<string> { reason };
                ImportSections.Add(new TransferSection(id, () => L.T("settings.transfer.skipped"), enabled: false, notes: () => why));
                continue;
            }
            var sp = plan.Section(id);
            bool risky = ps.Risky;
            Func<string>? warn = !risky ? null : id == SettingsSections.Hosts ? () => L.T("settings.transfer.warn_hosts_in") : () => L.T("settings.transfer.warn_dns_in");
            var skips = sp?.Items.Where(i => i.Action == PlanAction.Skip).Select(i => i.Reason is null ? i.Text : i.Text.Length > 0 ? i.Text + ": " + i.Reason : i.Reason).ToList() ?? [];
            skips.AddRange(problems);
            var notes = skips.Distinct().ToList();
            Func<IReadOnlyList<string>> notesFn = () => notes.Count <= 4 ? notes : [.. notes.Take(3), L.T("settings.transfer.more", new { n = notes.Count - 3 })];
            bool changes = sp?.Changes ?? false;
            var row = new TransferSection(id, sp is null ? () => L.T("settings.transfer.sum_none") : () => Summary(sp), risky, warn, ps.Items,
                enabled: true, notes: notesFn, linesLabel: id == SettingsSections.Hosts);
            // По умолчанию отмечено то, что что-то меняет; hosts и чужой DNS — только сознательно.
            row.IsChecked = was.TryGetValue(id, out var w) ? w : changes && !risky;
            row.Changed += () => Raise(nameof(CanImport));
            ImportSections.Add(row);
        }
        Raise(nameof(CanImport));
    }

    public bool CanImport => !Busy && HasPackage && ImportSections.Any(s => s.IsChecked);

    string _importResult = "";
    public string ImportResultText { get => _importResult; private set { if (Set(ref _importResult, value)) Raise(nameof(HasImportResult)); } }
    public bool HasImportResult => ImportResultText.Length > 0;

    public ICommand ImportCmd => _importCmd ??= RelayCommand.Async(ImportAsync, () => CanImport);
    ICommand? _importCmd;

    public async Task ImportAsync()
    {
        if (_pkg is not { Ok: true } pkg || _state is null) return;
        var chosen = ImportSections.Where(s => s.IsChecked).Select(s => s.Id).ToList();
        if (chosen.Count == 0) { ImportResultText = L.T("settings.transfer.nothing_chosen"); return; }
        var plan = SettingsImport.Plan(pkg, _state, chosen, ReplaceMode ? ImportMode.Replace : ImportMode.Add);
        if (!plan.HasChanges) { ImportResultText = L.T("settings.transfer.import_nochange"); return; }
        Busy = true; BusyText = L.T("settings.transfer.import_busy");
        ImportResultText = "";
        try
        {
            var c = await Need();
            if (c is null) { ImportResultText = L.T("settings.transfer.helper_missing"); return; }
            var r = await c.SettingsImportAsync(plan);
            var done = r.Sections.Where(s => s.Ok).Select(s => SettingsSections.Title(s.Id)).ToList();
            var lines = new List<string>();
            if (done.Count > 0) lines.Add(L.T("settings.transfer.import_done", new { list = string.Join(", ", done) }));
            foreach (var f in r.Sections.Where(s => !s.Ok))
                lines.Add(L.T("settings.transfer.section_failed", new { title = SettingsSections.Title(f.Id), reason = f.Message ?? "" }));
            if (r.JournalId is not null) lines.Add(L.T("settings.transfer.undo_hint"));
            if (lines.Count == 0) lines.Add(L.T("settings.transfer.import_nochange"));
            ImportResultText = string.Join(Environment.NewLine, lines);
            Say(done.Count > 0 ? L.T("settings.transfer.import_done", new { list = string.Join(", ", done) }) : "");
            await AfterImport();
            // Состояние поменялось: план строится заново, чтобы показать «уже есть».
            _state = await c.SettingsExportReadAsync();
            RebuildImport();
        }
        catch (Exception e) when (e is HelperException or HelperUnavailableException or IOException)
        {
            ImportResultText = L.T("settings.transfer.import_failed", new { error = Texts.Friendly(e) });
        }
        finally { Busy = false; BusyText = ""; Raise(nameof(CanImport)); CommandManager.InvalidateRequerySuggested(); }
    }
}
