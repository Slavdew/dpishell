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
    IReadOnlyList<string> Backups);

/// <summary>Итог перепроверки: что осталось, что снято (с причиной — строкой таблицы).</summary>
public sealed record HostsRecheckResult(IReadOnlyList<HostsEntry> Kept, IReadOnlyList<HostsCheckRow> Removed, HostsWriteResult Write, bool BlockLost);

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
        var r = File.Apply(entries);
        SaveApplied(entries);
        return r;
    }

    /// <summary>«Вернуть как было»: удалить только наш блок; чужие строки и правки других программ остаются.</summary>
    public HostsWriteResult Revert()
    {
        var r = File.RemoveBlock();
        SaveApplied([]);
        return r;
    }

    /// <summary>
    /// Перепроверка стоящих записей (раз в сутки и при старте): каждая проходит те же проверки, что при установке.
    /// Не прошла (адрес устарел, сертификат чужой, владелец не тот, домен пропал из списка) или DNS снова работает — снимается.
    /// Новых записей не добавляет.
    /// </summary>
    public async Task<HostsRecheckResult> RecheckAsync(CancellationToken ct = default)
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
        return new HostsStatus(File.HostsPath, block, text.Conflicts(), applied, at,
            BlockLost: applied.Count > 0 && block.Entries.Count == 0, readOnly, File.Backups());
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
