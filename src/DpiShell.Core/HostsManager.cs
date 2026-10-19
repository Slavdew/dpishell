using System.Net;
using System.Text.Json;

namespace DpiShell.Core;

/// <summary>Состояние «Починить адреса» для экрана hosts.</summary>
public sealed record HostsStatus(
    string HostsPath,
    HostsBlockInfo Block,
    IReadOnlyList<HostsConflict> Conflicts,
    /// <summary>Что мы записали в последний раз (пусто — ничего не стоит или блок убран).</summary>
    IReadOnlyList<HostsEntry> Applied,
    DateTime? AppliedAt,
    /// <summary>Мы записывали блок, а в файле его нет — снят Защитником или другой программой; предложить повторить.</summary>
    bool BlockLost,
    bool ReadOnly,
    IReadOnlyList<string> Backups,
    /// <summary>Свои строки пользователя, записанные нами (тег dpishell-user).</summary>
    IReadOnlyList<HostsUserLine>? UserLines = null,
    /// <summary>Чужие строки, которые мы закомментировали и можем вернуть.</summary>
    IReadOnlyList<HostsOffLine>? OffLines = null,
    /// <summary>Свои строки, адрес которых при последней перепроверке перестал работать (сами не снимаем — только сообщаем).</summary>
    IReadOnlyList<HostsEntry>? UserStale = null);

/// <summary>Итог перепроверки: что осталось, что снято (с причиной — строкой таблицы).</summary>
public sealed record HostsRecheckResult(IReadOnlyList<HostsEntry> Kept, IReadOnlyList<HostsCheckRow> Removed, HostsWriteResult Write, bool BlockLost,
    /// <summary>Свои строки с неработающим адресом (все) и те, о которых сообщаем впервые.</summary>
    IReadOnlyList<HostsEntry>? StaleUser = null, IReadOnlyList<HostsEntry>? NewStaleUser = null);

public sealed record HostsRejected(string Host, string Ip, string Reason);

/// <param name="Rejected">Новые строки, не прошедшие проверку адреса перед записью (записаны остальные).</param>
public sealed record HostsUserResult(bool Changed, IReadOnlyList<HostsEntry> Added, IReadOnlyList<HostsEntry> Removed, IReadOnlyList<HostsRejected> Rejected, string? Backup);

/// <param name="Skipped">Строки, которые не тронуты, с причиной.</param>
public sealed record HostsCommentResult(bool Changed, IReadOnlyList<HostsOffLine> Commented, IReadOnlyList<string> Skipped, string? Backup);

public sealed record HostsRestoreResult(bool Changed, int RemovedBlock, int RemovedUser, int Uncommented, string? Backup);

/// <summary>
/// «Починить адреса» (architecture.md «Починить адреса»): проверить кандидатов → предпросмотр → записать → «Вернуть как было» → перепроверка
/// раз в день / при старте. Функция выключена по умолчанию (AppState.HostsFix) — включение и вызовы решает окно.
/// </summary>
public sealed class HostsManager
{
    readonly AppPaths _paths;
    readonly Func<IEndpointProbe> _probe;
    readonly Func<(IHostsDns Dns, IDisposable? Owner)> _dns;
    readonly Func<(IRdapClient? Rdap, IDisposable? Owner)> _rdap;
    readonly Func<IReadOnlyList<HostsCandidate>> _candidates;

    /// <summary>Журнал изменений (откат всего): запись — до правки файла hosts.</summary>
    public IChangeRecorder Recorder { get; set; } = NullRecorder.Instance;

    public HostsFileStore File { get; }
    public string Dir => Path.Combine(_paths.Root, "hosts");
    string AppliedFile => Path.Combine(Dir, "applied.json");

    /// <param name="hostsPath">null — системный hosts (и после записи сбрасывается кэш DNS). Тесты передают копию во временной папке.</param>
    public HostsManager(AppPaths paths, string? hostsPath = null,
        IEndpointProbe? probe = null, IHostsDns? dns = null, IRdapClient? rdap = null,
        Func<IReadOnlyList<HostsCandidate>>? candidates = null)
    {
        _paths = paths;
        File = new HostsFileStore(hostsPath ?? HostsFileStore.SystemHostsPath, Dir,
            hostsPath is null ? () => DnsCache.Flush() : null);
        _probe = () => probe ?? new TlsEndpointProbe();
        _dns = dns is not null ? () => (dns, null) : () => { var d = new DohResolver(); return (new DohHostsDns(d), d); };
        _rdap = rdap is not null ? () => (rdap, null) : () => { var r = new RdapClient(); return (r, r); };
        _candidates = candidates ?? (() => HostsCandidates.LoadSeed(paths));
    }

    /// <summary>Кандидаты из поставки (с проверкой хеша и белого списка).</summary>
    public IReadOnlyList<HostsCandidate> Candidates() => _candidates();

    /// <summary>«Проверить адреса»: таблица по всем кандидатам. Ничего не пишет.</summary>
    public Task<HostsCheckReport> CheckAsync(IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default) =>
        CheckAsync(Candidates(), progress, ct);

    async Task<HostsCheckReport> CheckAsync(IEnumerable<HostsCandidate> list, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var (dns, dnsOwner) = _dns();
        var (rdap, rdapOwner) = _rdap();
        try { return await new HostsVerifier(_probe(), dns, rdap).CheckAsync(list, progress, ct); }
        finally { dnsOwner?.Dispose(); rdapOwner?.Dispose(); }
    }

    /// <summary>Что будет добавлено/удалено, без записи. Пустой список — предпросмотр удаления блока.</summary>
    public HostsPreview Preview(IEnumerable<HostsEntry> entries) => File.Preview(entries);

    /// <summary>
    /// «Записать N адресов»: только записи из отчёта проверки с вердиктом Install (остальное отбрасывается тут же,
    /// окно не может записать непроверенное). Пустой отчёт — «нечего чинить», блок убирается, если был.
    /// </summary>
    public HostsWriteResult Apply(HostsCheckReport report)
    {
        var entries = report.ToInstall;
        string rec = "";
        // Блок правят повторно — новая запись не нужна: откат один, «убрать блок».
        if (entries.Count > 0 && Recorder.Active(ChangeKinds.HostsBlock).Count == 0)
            rec = Recorder.Begin(ChangeKinds.HostsBlock, $"Записаны проверенные адреса в файл hosts: {entries.Count}",
                new System.Text.Json.Nodes.JsonObject { ["block"] = false }, new System.Text.Json.Nodes.JsonObject { ["count"] = entries.Count });
        HostsWriteResult r;
        try { r = File.Apply(entries); }
        catch { Recorder.Abort(rec); throw; }
        if (rec.Length > 0) { if (r.Changed) Recorder.Done(rec); else Recorder.Abort(rec); }
        SaveApplied(entries);
        return r;
    }

    /// <summary>«Вернуть как было»: удалить только наш блок; чужие строки и правки других программ остаются.</summary>
    public HostsWriteResult Revert()
    {
        var r = File.RemoveBlock();
        SaveApplied([]);
        Recorder.Close(ChangeKinds.HostsBlock, "блок убран кнопкой «Вернуть как было»");
        return r;
    }

    /// <summary>
    /// Перепроверка стоящих записей (раз в сутки и при старте): каждая проходит те же проверки, что при установке.
    /// Не прошла (адрес устарел, сертификат чужой, владелец не тот, домен пропал из списка) или DNS снова работает — снимается.
    /// Новых записей не добавляет.
    /// </summary>
    public async Task<HostsRecheckResult> RecheckAsync(CancellationToken ct = default)
    {
        var r = await RecheckBlockAsync(ct);
        var (stale, fresh) = await RecheckUserAsync(ct);
        return r with { StaleUser = stale, NewStaleUser = fresh };
    }

    async Task<HostsRecheckResult> RecheckBlockAsync(CancellationToken ct)
    {
        var status = Status();
        var current = status.Block.Entries;
        if (current.Count == 0)
        {
            // Блока нет: либо нечего было ставить, либо его снял Защитник — сообщаем, но сами не возвращаем.
            if (status.Block.Present) return new([], [], File.RemoveBlock(), status.BlockLost);   // пустой/битый блок — убрать
            return new([], [], new HostsWriteResult(false, [], [], null), status.BlockLost);
        }

        var known = Candidates().ToDictionary(c => c.Host, StringComparer.OrdinalIgnoreCase);
        var removed = new List<HostsCheckRow>();
        var toCheck = new List<HostsCandidate>();
        foreach (var g in current.GroupBy(e => e.Host, StringComparer.OrdinalIgnoreCase))
        {
            if (!known.TryGetValue(g.Key, out var c))
            {
                removed.AddRange(g.Select(e => new HostsCheckRow(e.Host, e.Ip, HostsVerdict.Rejected, null, OwnerState.Unknown, null, DnsState.Unknown, "нет в списке кандидатов")));
                continue;
            }
            var ips = g.Select(e => e.Ip).Where(ip => c.Ips.Contains(ip, StringComparer.OrdinalIgnoreCase)).ToList();
            removed.AddRange(g.Where(e => !ips.Contains(e.Ip, StringComparer.OrdinalIgnoreCase))
                .Select(e => new HostsCheckRow(e.Host, e.Ip, HostsVerdict.Rejected, null, OwnerState.Unknown, null, DnsState.Unknown, "адреса нет в списке кандидатов")));
            if (ips.Count > 0) toCheck.Add(c with { Ips = ips });
        }

        var report = await CheckAsync(toCheck, null, ct);
        removed.AddRange(report.Rows.Where(r => !r.Install));
        var keep = report.ToInstall;
        var write = File.Apply(keep);
        SaveApplied(keep);
        return new(keep, removed, write, false);
    }

    public HostsStatus Status()
    {
        var text = File.Read();
        var block = text.Block();
        var (applied, at) = LoadApplied();
        bool readOnly = System.IO.File.Exists(File.HostsPath) && System.IO.File.GetAttributes(File.HostsPath).HasFlag(FileAttributes.ReadOnly);
        var user = text.UserLines();
        var current = user.Select(l => new HostsEntry(l.Host, l.Ip)).ToHashSet();
        return new HostsStatus(File.HostsPath, block, text.Conflicts(), applied, at,
            BlockLost: applied.Count > 0 && block.Entries.Count == 0, readOnly, File.Backups(),
            user, text.OffLines(), [.. LoadStale().Where(current.Contains)]);
    }

    // ---------- примерка строк, аудит, свои строки, комментирование (ТЗ 12) ----------

    /// <summary>
    /// «Примерить»: вердикт для каждой строки из вставки. Файл hosts не меняется. Строк больше <see cref="HostsPaste.MaxEntries"/> не берём.
    /// </summary>
    public async Task<HostsTryReport> TryAsync(IReadOnlyList<HostsEntry> entries, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var items = entries.Take(HostsPaste.MaxEntries).Select(e => new HostsTryItem(e.Host, e.Ip)).ToList();
        return await RunTryAsync(items, 0, entries.Count > HostsPaste.MaxEntries, entries.Count, progress, ct);
    }

    async Task<HostsTryReport> RunTryAsync(IReadOnlyList<HostsTryItem> items, int skipped, bool truncated, int total,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var (dns, dnsOwner) = _dns();
        var (rdap, rdapOwner) = _rdap();
        try { return await new HostsTryout(_probe(), dns, rdap).TryAsync(items, skipped, truncated, total, progress, ct); }
        finally { dnsOwner?.Dispose(); rdapOwner?.Dispose(); }
    }

    /// <summary>
    /// «Проверить мой hosts»: каждая запись чужих строк (вне наших блоков) — по тем же правилам, что и примерка. Первыми идут строки с настоящим
    /// адресом (их проверка важнее), затем заглушки; больше <see cref="HostsPaste.MaxEntries"/> записей не проверяем — «проверено N из M».
    /// </summary>
    public async Task<HostsTryReport> AuditAsync(IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var all = File.Read().ForeignLines()
            .SelectMany(l => l.Entries.Select(e => new HostsTryItem(e.Host, e.Ip, l.Line, l.Id))).ToList();
        var items = all.OrderBy(i => HostsRules.TryParsePublic(i.Ip, out _) ? 0 : 1).Take(HostsPaste.MaxEntries).OrderBy(i => i.Line).ToList();
        return await RunTryAsync(items, 0, all.Count > items.Count, all.Count, progress, ct);
    }

    /// <summary>
    /// «Записать выбранные»: поставить ровно эти свои строки. Прежние остаются как есть (даже устаревшие — их выбрал пользователь),
    /// каждая новая заново проверяется живым подключением (сертификат на это имя) — помощник пишет только то, что проверил сам.
    /// </summary>
    public async Task<HostsUserResult> SetUserAsync(IReadOnlyList<HostsEntry> wanted, CancellationToken ct = default)
    {
        var existing = File.Read().UserLines().Select(l => new HostsEntry(l.Host, l.Ip)).ToHashSet();
        var rejected = new List<HostsRejected>();
        var final = new List<HostsEntry>();
        var fresh = new List<HostsEntry>();
        foreach (var e in wanted.Select(e => new HostsEntry(HostsRules.Normalize(e.Host), e.Ip.Trim())).Distinct())
        {
            if (existing.Contains(e)) { final.Add(e); continue; }
            if (!HostsRules.TryParsePublic(e.Ip, out _) || HostsInput.CleanHost(e.Host) != e.Host)
            { rejected.Add(new(e.Host, e.Ip, "не публичный адрес или не имя сайта")); continue; }
            fresh.Add(e);
        }
        var probe = _probe();
        var gate = new SemaphoreSlim(12);
        var checks = await Task.WhenAll(fresh.Select(async e =>
        {
            await gate.WaitAsync(ct);
            try { return await probe.CheckAsync(e.Host, IPAddress.Parse(e.Ip), ct); }
            finally { gate.Release(); }
        }));
        for (int i = 0; i < fresh.Count; i++)
        {
            if (checks[i].Ok) final.Add(fresh[i]);
            else rejected.Add(new(fresh[i].Host, fresh[i].Ip, checks[i].Outcome == EndpointOutcome.BadCertificate ? "сертификат выдан не этому сайту" : "адрес не отвечает"));
        }
        string rec = "";
        if (final.Count > 0 && Recorder.Active(ChangeKinds.HostsUser).Count == 0)
            rec = Recorder.Begin(ChangeKinds.HostsUser, $"Записаны свои строки в файл hosts: {final.Count}",
                new System.Text.Json.Nodes.JsonObject { ["lines"] = 0 }, new System.Text.Json.Nodes.JsonObject { ["count"] = final.Count });
        (HostsUserChange change, bool changed, string? backup) w;
        try { w = File.ApplyUser(final); }
        catch { Recorder.Abort(rec); throw; }
        var (change, changed, backup) = w;
        if (rec.Length > 0) { if (changed) Recorder.Done(rec); else Recorder.Abort(rec); }
        if (final.Count == 0) Recorder.Close(ChangeKinds.HostsUser, "свои строки убраны из hosts");
        KeepStale(final);
        return new HostsUserResult(changed, change.Added, change.Removed, rejected, backup);
    }

    /// <summary>
    /// «Закомментировать выбранные»: строки по «номер:хеш». <paramref name="allowed"/> — какие строки вообще можно трогать
    /// (помощник передаёт мешающие из последней проверки; null — любые чужие). Хеш не совпал — строка изменилась после проверки, пропуск.
    /// </summary>
    public HostsCommentResult Comment(IEnumerable<string> ids, ISet<string>? allowed = null)
    {
        var list = ids.ToList();
        var refused = allowed is null ? [] : list.Where(i => !allowed.Contains(i)).Select(i => $"{i} — строка не из последней проверки").ToList();
        var ids2 = allowed is null ? list : list.Where(allowed.Contains).ToList();
        var rec = ids2.Count > 0
            ? Recorder.Begin(ChangeKinds.HostsComment, $"Закомментированы чужие строки в файле hosts (до {ids2.Count})",
                new System.Text.Json.Nodes.JsonObject { ["commented"] = false }, new System.Text.Json.Nodes.JsonObject { ["count"] = ids2.Count })
            : "";
        (HostsCommentChange c, bool changed, string? backup) w;
        try { w = File.Comment(ids2); }
        catch { Recorder.Abort(rec); throw; }
        var (c, changed, backup) = w;
        if (rec.Length > 0) { if (changed) Recorder.Done(rec); else Recorder.Abort(rec); }
        return new HostsCommentResult(changed, c.Commented, [.. refused, .. c.Skipped], backup);
    }

    public HostsCommentResult Uncomment()
    {
        var (restored, changed, backup) = File.Uncomment();
        Recorder.Close(ChangeKinds.HostsComment, "строки возвращены кнопкой «Вернуть закомментированные»");
        return new HostsCommentResult(changed, restored, [], backup);
    }

    /// <summary>«Вернуть как было»: убрать оба наших блока и свои строки, раскомментировать закомментированные нами.</summary>
    public HostsRestoreResult RestoreAll()
    {
        var (c, changed, backup) = File.RestoreAll();
        SaveApplied([]);
        KeepStale([]);
        foreach (var k in new[] { ChangeKinds.HostsBlock, ChangeKinds.HostsUser, ChangeKinds.HostsComment })
            Recorder.Close(k, "hosts возвращён кнопкой «Вернуть как было»");
        return new HostsRestoreResult(changed, c.RemovedBlock.Count, c.RemovedUser.Count, c.Uncommented, backup);
    }

    /// <summary>
    /// Перепроверка своих строк: адрес должен по-прежнему отвечать с сертификатом этого сайта. Не отвечает — НЕ снимаем
    /// (строку выбрал человек), а запоминаем и сообщаем. Возвращает все устаревшие и те, о которых говорим впервые.
    /// </summary>
    async Task<(IReadOnlyList<HostsEntry> Stale, IReadOnlyList<HostsEntry> New)> RecheckUserAsync(CancellationToken ct)
    {
        var mine = File.Read().UserLines().Select(l => new HostsEntry(l.Host, l.Ip)).Distinct().ToList();
        var before = LoadStale().ToHashSet();
        if (mine.Count == 0) { KeepStale([]); return ([], []); }
        var probe = _probe();
        var gate = new SemaphoreSlim(8);
        var checks = await Task.WhenAll(mine.Select(async e =>
        {
            await gate.WaitAsync(ct);
            try { return IPAddress.TryParse(e.Ip, out var ip) ? await probe.CheckAsync(e.Host, ip, ct) : new EndpointCheck(EndpointOutcome.NoConnect, 0, "?"); }
            finally { gate.Release(); }
        }));
        var stale = mine.Where((_, i) => !checks[i].Ok).ToList();
        SaveStale(stale);
        return (stale, stale.Where(e => !before.Contains(e)).ToList());
    }

    string StaleFile => Path.Combine(Dir, "user-stale.json");

    void KeepStale(IEnumerable<HostsEntry> current)
    {
        var keep = current.ToHashSet();
        var stale = LoadStale();
        if (stale.Any(e => !keep.Contains(e))) SaveStale([.. stale.Where(keep.Contains)]);
    }

    void SaveStale(IReadOnlyList<HostsEntry> stale)
    {
        Directory.CreateDirectory(Dir);
        var tmp = StaleFile + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(stale, Json));
        System.IO.File.Move(tmp, StaleFile, overwrite: true);
    }

    IReadOnlyList<HostsEntry> LoadStale()
    {
        try { return System.IO.File.Exists(StaleFile) ? JsonSerializer.Deserialize<List<HostsEntry>>(System.IO.File.ReadAllText(StaleFile), Json) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Полное восстановление из копии — только по явной просьбе, после показа <see cref="HostsFileStore.DiffWithBackup"/>.</summary>
    public string? RestoreBackup(string backup)
    {
        var r = File.RestoreBackup(backup);
        SaveApplied(File.Read().Block().Entries);
        return r;
    }

    sealed record AppliedRecord(DateTime At, List<HostsEntry> Entries);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    void SaveApplied(IReadOnlyList<HostsEntry> entries)
    {
        Directory.CreateDirectory(Dir);
        var tmp = AppliedFile + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(new AppliedRecord(DateTime.Now, [.. entries]), Json));
        System.IO.File.Move(tmp, AppliedFile, overwrite: true);
    }

    (IReadOnlyList<HostsEntry>, DateTime?) LoadApplied()
    {
        try
        {
            if (!System.IO.File.Exists(AppliedFile)) return ([], null);
            var r = JsonSerializer.Deserialize<AppliedRecord>(System.IO.File.ReadAllText(AppliedFile), Json);
            return r is null ? ([], null) : (r.Entries ?? [], r.At);
        }
        catch (JsonException) { return ([], null); }
    }
}
