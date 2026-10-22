namespace DpiShell.Core;

/// <summary>Где что лежит. Корень — %ProgramData%\DpiShell: служба читает файлы без профиля пользователя.</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string Core => Path.Combine(Root, "core");
    public string Lists => Path.Combine(Root, "lists");
    public string Fake => Path.Combine(Root, "fake");
    public string Logs => Path.Combine(Root, "logs");
    public string Results => Path.Combine(Root, "results");
    public string StateFile => Path.Combine(Root, "state.json");
    public string Winws => Path.Combine(Core, "winws.exe");
    /// <summary>Прежние настройки DNS адаптеров до первой смены (architecture.md «DNS этого компьютера») — для «Вернуть как было».</summary>
    public string DnsBackup => Path.Combine(Root, "dns-backup.json");

    /// <summary>Прежний системный прокси машины до «Очистить» (вкладка «Прокси») — для «Вернуть прежний».</summary>
    public string ProxyBackup => Path.Combine(Root, "proxy-backup.json");

    /// <summary>Журнал изменений системы (откат всего): что меняли, прежнее значение, чем откатить. Пишет помощник.</summary>
    public string ChangesFile => Path.Combine(Root, "changes.jsonl");

    /// <summary>Данные, приехавшие вместе с программой (data\ рядом с exe).</summary>
    public string Package { get; }

    /// <summary>Обновлённые данные (ставит помощник по data.json, architecture.md «Обновления данных») — в закрытой папке программы.</summary>
    public string Updates => Path.Combine(Root, "data");

    /// <summary>
    /// Откуда читать данные: обновлённые, если они новее поставки и целы (хеши сходятся с их data.json), иначе — поставка.
    /// Окну без прав папка программы закрыта — у него всегда поставка (данные помощника — через status).
    /// </summary>
    public string Seed => _seed ??= ResolveSeed();
    string? _seed;

    /// <summary>Перечитать, откуда брать данные (после установки обновления).</summary>
    public void ResetSeed() => _seed = null;

    /// <summary>Манифест данных, которые сейчас в работе; null — его нет.</summary>
    public DataManifest? SeedManifest
    {
        get
        {
            var f = Path.Combine(Seed, DataManifest.FileName);
            try { return File.Exists(f) ? DataManifest.Load(f) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { return null; }
        }
    }

    public AppPaths(string? root = null, string? seed = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DpiShell");
        Package = seed ?? Path.Combine(AppContext.BaseDirectory, "data");
    }

    string ResolveSeed()
    {
        try
        {
            var upd = Path.Combine(Updates, DataManifest.FileName);
            if (!File.Exists(upd)) return Package;
            var u = DataManifest.Load(upd);
            var pkg = Path.Combine(Package, DataManifest.FileName);
            if (File.Exists(pkg) && DataManifest.CompareVersions(u.Version, DataManifest.Load(pkg).Version) <= 0) return Package;
            // Повреждено (антивирус, ручная правка) — поставка: она проверена при выпуске.
            return u.Mismatches(Updates).Count == 0 ? Updates : Package;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return Package;
        }
    }

    public void EnsureDirectories()
    {
        foreach (var d in new[] { Root, Core, Lists, Fake, Logs, Results })
            Directory.CreateDirectory(d);
    }

    /// <summary>
    /// Доступ к папке программы — только системе и администраторам (по SID, на любом языке Windows).
    /// По умолчанию в %ProgramData% пользователи могут читать и писать: это раскрывало свои сайты и позволяло
    /// подменить winws.exe, который служба запускает от имени системы после перезагрузки.
    /// Наследование от %ProgramData% отключается; вложенные папки и файлы получают те же права и владельца «Администраторы».
    /// До установки помощника любой пользователь мог создать папку сам или положить в неё ссылки (junction, symlink, жёсткие):
    /// корень-ссылка удаляется, корень с чужим владельцем откладывается в сторону (<c>DpiShell.untrusted-…</c>) и создаётся заново,
    /// ссылки внутри удаляются (сами ссылки, не то, на что они указывают); папка была открыта — скачанные данные (<c>data\</c>)
    /// удаляются: их мог положить кто угодно, в работе останется поставка. Возвращает, что убрано (для журнала).
    /// </summary>
    public IReadOnlyList<string> Harden()
    {
        var removed = new List<string>();
        var rootAttr = SafeFolder.Attributes(Root);
        if (rootAttr is { } ra && (ra & FileAttributes.ReparsePoint) != 0)
        {
            SafeFolder.DeleteLink(Root, ra);
            removed.Add(Root);
        }
        else if (rootAttr is not null && !SafeFolder.TrustedOwner(Root))
        {
            var aside = $"{Root}.untrusted-{DateTime.Now:yyyyMMdd-HHmmss}";
            Directory.Move(Root, aside);
            removed.Add(Root + " → " + aside);
        }
        bool wasOpen = Directory.Exists(Root) && !SafeFolder.AclClosed(Root);

        if (Directory.Exists(Root)) SafeFolder.RemoveLinks(Root, removed);
        var acl = SafeFolder.ClosedAcl(setOwner: IsElevated());
        if (Directory.Exists(Root)) new DirectoryInfo(Root).SetAccessControl(acl);
        else new DirectoryInfo(Root).Create(acl);

        // Записи, созданные до этого: ссылки — удалить, остальным — владелец «Администраторы» и наследуемые права.
        SafeFolder.Sanitize(Root, setOwner: IsElevated(), removed);
        if (wasOpen && Directory.Exists(Updates))
        {
            Directory.Delete(Updates, recursive: true);
            removed.Add(Updates);
            ResetSeed();
        }
        EnsureDirectories();
        return removed;
    }

    /// <summary>
    /// «Удалить DpiShell с этого ПК»: стереть папку программы (<see cref="Root"/>) целиком — только этот точный путь, по ссылкам не заходя
    /// (<see cref="SafeFolder.DeleteTree"/>). Отложенные в сторону чужие папки рядом (<c>DpiShell.untrusted-…</c>) не трогаются.
    /// Файл может держать только что остановленная служба или антивирус — повтор до <paramref name="timeout"/>, потом — исходная ошибка.
    /// </summary>
    public void Purge(TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            try { SafeFolder.DeleteTree(Root); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && DateTime.UtcNow < until) { Thread.Sleep(300); }
        }
    }

    static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Закрыта ли папка: корень — не ссылка, владелец — система, администраторы или этот же процесс, обычным пользователям
    /// доступа нет; внутри нет ссылок и записей чужих владельцев (для старта помощника, установки и тестов).
    /// </summary>
    public bool IsHardened()
    {
        try
        {
            if (SafeFolder.Attributes(Root) is not { } a || (a & FileAttributes.ReparsePoint) != 0 || (a & FileAttributes.Directory) == 0) return false;
            if (!SafeFolder.TrustedOwner(Root) || !SafeFolder.AclClosed(Root)) return false;
            return SafeFolder.TreeIsClean(Root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }
}
