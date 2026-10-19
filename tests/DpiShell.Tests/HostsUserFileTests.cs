using System.Net;
using System.Text;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>
/// Свои строки, комментирование чужих и «Вернуть как было» на копии hosts во временной папке (ТЗ 12, «Готово, когда»).
/// Главное правило: чужие строки и строки других программ — теми же байтами, в любой кодировке файла.
/// </summary>
public sealed class HostsUserFileTests : IDisposable
{
    readonly TempDir _t = new("-hosts-user");
    readonly string _hosts;
    readonly HostsFileStore _store;
    static readonly DateTime At = new(2026, 10, 19);

    static readonly HostsEntry Gh = new("github.com", "140.82.121.3");
    static readonly HostsEntry Raw = new("raw.githubusercontent.com", "146.75.22.132");

    public HostsUserFileTests()
    {
        _hosts = _t.Sub("etc\\hosts");
        Directory.CreateDirectory(Path.GetDirectoryName(_hosts)!);
        _store = new HostsFileStore(_hosts, _t.Sub("data\\hosts"));
    }

    public void Dispose() => _t.Dispose();

    const string Base = "# Copyright (c) 1993-2009 Microsoft Corp.\r\n127.0.0.1 localhost\r\n0.0.0.0 ads.example.com\r\n10.1.2.3 nas.example.com\r\n1.2.3.4 broken.example.com\r\n";

    byte[] Bytes => File.ReadAllBytes(_hosts);

    string IdOf(string textLine) => HostsText.Decode(Bytes).ForeignLines().First(l => l.Text.Trim() == textLine).Id;

    // ---------- свои строки ----------

    [Fact]
    public void UserSet_WritesOwnBlock_WithTag_AndKeepsForeignBytes()
    {
        File.WriteAllText(_hosts, Base, Encoding.Latin1);
        var (change, changed, backup) = _store.ApplyUser([Gh, Raw], At);

        Assert.True(changed);
        Assert.Equal([Gh, Raw], change.Added);
        Assert.NotNull(backup);
        var text = File.ReadAllText(_hosts, Encoding.Latin1);
        Assert.Equal(Base +
            "# DpiShell user begin 2026-10-19\r\n" +
            "# Managed by DpiShell (own lines). Every line below is tagged dpishell-user; the app can remove them.\r\n" +
            "140.82.121.3 github.com  # dpishell-user 2026-10-19 manual\r\n" +
            "146.75.22.132 raw.githubusercontent.com  # dpishell-user 2026-10-19 manual\r\n" +
            "# DpiShell user end\r\n", text);
        // Свои строки читаются обратно, а в «чужие» (аудит, конфликты) не попадают.
        var parsed = HostsText.Decode(Bytes);
        Assert.Equal(["github.com", "raw.githubusercontent.com"], parsed.UserLines().Select(l => l.Host));
        Assert.All(parsed.UserLines(), l => Assert.Equal("manual", l.Source));
        Assert.Empty(parsed.Conflicts());
        Assert.DoesNotContain(parsed.ForeignLines(), l => l.Text.Contains("github"));
    }

    [Fact]
    public void UserSet_SameSet_DoesNotRewriteFile_AndKeepsOldDates()
    {
        File.WriteAllText(_hosts, Base, Encoding.Latin1);
        _store.ApplyUser([Gh], At);
        var before = Bytes;
        var again = _store.ApplyUser([Gh], At.AddDays(5));
        Assert.False(again.Changed);
        Assert.Equal(before, Bytes);

        // Добавили вторую строку: у первой дата прежняя.
        _store.ApplyUser([Gh, Raw], At.AddDays(5));
        var lines = HostsText.Decode(Bytes).UserLines();
        Assert.Equal(["2026-10-19", "2026-10-24"], lines.Select(l => l.Date));
    }

    [Fact]
    public void UserSet_Empty_RemovesOnlyOwnLines()
    {
        File.WriteAllText(_hosts, Base, Encoding.Latin1);
        _store.ApplyUser([Gh], At);
        var r = _store.ApplyUser([], At);
        Assert.True(r.Changed);
        Assert.Equal(Base, File.ReadAllText(_hosts, Encoding.Latin1));
    }

    [Fact]
    public void UserSet_DoesNotTouchWhitelistBlock_AndBackwards()
    {
        File.WriteAllText(_hosts, Base, Encoding.Latin1);
        _store.Apply([Gh], At);
        _store.ApplyUser([Raw], At);
        var both = File.ReadAllText(_hosts, Encoding.Latin1);
        Assert.Contains("# DpiShell begin v1", both);
        Assert.Contains("# DpiShell user begin", both);

        // Блок белого списка пересобирается — свои строки на месте; и наоборот.
        _store.Apply([], At);
        Assert.Equal(["raw.githubusercontent.com"], HostsText.Decode(Bytes).UserLines().Select(l => l.Host));
        Assert.DoesNotContain("# DpiShell begin", File.ReadAllText(_hosts, Encoding.Latin1));
        _store.Apply([Gh], At);
        _store.ApplyUser([], At);
        var t = HostsText.Decode(Bytes);
        Assert.Empty(t.UserLines());
        Assert.Equal([Gh], t.Block().Entries);
    }

    [Fact]
    public void TaggedLine_IsRecognised_EvenWhenMovedOrFrameBroken()
    {
        File.WriteAllText(_hosts, "# DpiShell user begin 2026-10-19\r\n1.2.3.4 mine.example.com  # dpishell-user 2026-10-19 manual\r\n" +
            "127.0.0.1 localhost\r\n5.6.7.8 other.example.com  # dpishell-user 2026-10-18 manual\r\n", Encoding.Latin1);   // end нет
        var t = HostsText.Decode(Bytes);
        Assert.Equal(["mine.example.com", "other.example.com"], t.UserLines().Select(l => l.Host));

        var r = _store.RestoreAll();
        Assert.True(r.Changed);
        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(_hosts, Encoding.Latin1));
    }

    [Fact]
    public void UserLinesBetweenOurFrame_ThatWeDidNotWrite_AreKept()
    {
        File.WriteAllText(_hosts, Base, Encoding.Latin1);
        _store.ApplyUser([Gh], At);
        var text = File.ReadAllText(_hosts, Encoding.Latin1).Replace("# DpiShell user end", "9.9.9.9 handwritten.example.com\r\n# DpiShell user end");
        File.WriteAllText(_hosts, text, Encoding.Latin1);

        _store.ApplyUser([], At);
        Assert.Equal(Base + "9.9.9.9 handwritten.example.com\r\n", File.ReadAllText(_hosts, Encoding.Latin1));
    }

    // ---------- комментирование ----------

    [Fact]
    public void Comment_ByIdAndHash_CommentsOnlyThatLine_AndUncommentRestoresBytes()
    {
        File.WriteAllText(_hosts, Base, Encoding.Latin1);
        var id = IdOf("1.2.3.4 broken.example.com");
        var r = _store.Comment([id], At);
        Assert.True(r.Changed);
        Assert.Single(r.Change.Commented);
        Assert.Empty(r.Change.Skipped);
        Assert.Equal(Base.Replace("1.2.3.4 broken.example.com", "# DpiShell off 2026-10-19: 1.2.3.4 broken.example.com"),
            File.ReadAllText(_hosts, Encoding.Latin1));
        Assert.Equal(["1.2.3.4 broken.example.com"], HostsText.Decode(Bytes).OffLines().Select(o => o.Original));
        // Закомментированная строка больше не «чужая» запись.
        Assert.DoesNotContain(HostsText.Decode(Bytes).ForeignLines(), l => l.Text.Contains("broken"));

        var u = _store.Uncomment();
        Assert.True(u.Changed);
        Assert.Equal(Base, File.ReadAllText(_hosts, Encoding.Latin1));
    }

    [Fact]
    public void Comment_ChangedLine_OurLine_AndGarbageIds_AreSkippedWithReason()
    {
        File.WriteAllText(_hosts, Base, Encoding.Latin1);
        _store.Apply([Gh], At);
        var staleId = IdOf("1.2.3.4 broken.example.com");
        // Файл поменяли после проверки: тот же номер, другой текст.
        File.WriteAllText(_hosts, File.ReadAllText(_hosts, Encoding.Latin1).Replace("1.2.3.4 broken", "1.2.3.5 broken"), Encoding.Latin1);
        var lines = File.ReadAllLines(_hosts);
        int blockLine = Array.FindIndex(lines, l => l.StartsWith("140.82.121.3")) + 1;
        var ourId = HostsText.LineId(blockLine, lines[blockLine - 1]);

        var before = Bytes;
        var r = _store.Comment([staleId, ourId, "99:000000000000", "x"], At);
        Assert.False(r.Changed);
        Assert.Equal(4, r.Change.Skipped.Count);
        Assert.Contains("изменилась", r.Change.Skipped[0]);
        Assert.Contains("не чужая", r.Change.Skipped[1]);
        Assert.Equal(before, Bytes);
    }

    // ---------- «Вернуть как было» ----------

    /// <summary>Кодировки hosts: перевод строки, BOM, UTF-16, байты вне ASCII в чужих строках, нет перевода в конце.</summary>
    public static TheoryData<string> Variants => new() { "latin1-crlf", "latin1-lf", "latin1-highbytes", "utf8-bom", "utf16le-bom", "utf16be-bom", "mixed-eol" };

    static (Encoding Enc, byte[] Preamble, string Text) Make(string kind)
    {
        const string body = "# Copyright\r\n127.0.0.1 localhost\r\n0.0.0.0 ads.example.com\r\n10.1.2.3 nas.example.com\r\n1.2.3.4 broken.example.com\r\n";
        return kind switch
        {
            "latin1-crlf" => (Encoding.Latin1, [], body),
            "latin1-lf" => (Encoding.Latin1, [], body.Replace("\r\n", "\n")),
            "latin1-highbytes" => (Encoding.Latin1, [], body.Replace("# Copyright", "# café ÄÖ (ü)")),
            "utf8-bom" => (Encoding.Latin1, [0xEF, 0xBB, 0xBF], body.Replace("# Copyright", "# Ð·Ð°Ð¼ÐµÑ\u0082ÐºÐ°")),   // «заметка» в UTF-8, байты как Latin-1
            "utf16le-bom" => (new UnicodeEncoding(false, false), [0xFF, 0xFE], body.Replace("# Copyright", "# заметка")),
            "utf16be-bom" => (new UnicodeEncoding(true, false), [0xFE, 0xFF], body.Replace("# Copyright", "# заметка")),
            _ => (Encoding.Latin1, [], "# Copyright\n127.0.0.1 localhost\r\n0.0.0.0 ads.example.com\n10.1.2.3 nas.example.com\r\n1.2.3.4 broken.example.com\r\n"),
        };
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void WriteOwn_CommentForeign_OtherProgramAppends_RestoreAll_IsByteForByte(string kind)
    {
        var (enc, pre, text) = Make(kind);
        byte[] Encode(string s) => enc.GetBytes(s);
        var original = pre.Concat(enc.GetBytes(text)).ToArray();
        File.WriteAllBytes(_hosts, original);

        // 1. Свои строки и блок белого списка.
        _store.ApplyUser([Gh, Raw], At);
        _store.Apply([new HostsEntry("discord.com", "162.159.138.232")], At);
        // 2. Закомментировать чужую.
        var id = HostsText.Decode(Bytes).ForeignLines().First(l => l.Text.Contains("broken")).Id;
        Assert.True(_store.Comment([id], At).Changed);
        // 3. Другая программа дописывает свою строку в конец (в той же кодировке).
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var other = Encode("8.8.4.4 dns.other.example.com" + nl);
        File.WriteAllBytes(_hosts, [.. Bytes, .. other]);

        // До отката файл изменён.
        Assert.NotEqual(original, Bytes);
        // 4. «Вернуть как было»: исходник плюс строка другой программы, байт в байт.
        var r = _store.RestoreAll();
        Assert.True(r.Changed);
        Assert.Equal([.. original, .. other], Bytes);
        Assert.Equal(2, r.Change.RemovedUser.Count);
        Assert.Single(r.Change.RemovedBlock);
        Assert.Equal(1, r.Change.Uncommented);

        // Повтор ничего не делает.
        Assert.False(_store.RestoreAll().Changed);
    }

    [Fact]
    public void RestoreAll_WithNothingOfOurs_DoesNotTouchFile()
    {
        File.WriteAllText(_hosts, Base, Encoding.Latin1);
        var r = _store.RestoreAll();
        Assert.False(r.Changed);
        Assert.Null(r.Backup);
        Assert.Equal(Base, File.ReadAllText(_hosts, Encoding.Latin1));
    }

    [Fact]
    public void ForeignLines_ExcludeOwnBlocksAndOffLines_AndLocalhost()
    {
        File.WriteAllText(_hosts, Base, Encoding.Latin1);
        _store.Apply([Gh], At);
        _store.ApplyUser([Raw], At);
        var id = IdOf("1.2.3.4 broken.example.com");
        _store.Comment([id], At);
        var foreign = HostsText.Decode(Bytes).ForeignLines();
        Assert.Equal(["0.0.0.0 ads.example.com", "10.1.2.3 nas.example.com"], foreign.Select(l => l.Text));
    }

    [Fact]
    public void LineId_Format_AndParse()
    {
        var id = HostsText.LineId(7, "1.2.3.4 a.example.com");
        Assert.Matches(@"^7:[0-9a-f]{12}$", id);
        Assert.True(HostsText.TryParseId(id, out var n, out var h));
        Assert.Equal(7, n);
        Assert.Equal(HostsText.LineHash("1.2.3.4 a.example.com"), h);
        Assert.False(HostsText.TryParseId("0:abcdef012345", out _, out _));
        Assert.False(HostsText.TryParseId("7:ABCDEF012345", out _, out _));
        Assert.False(HostsText.TryParseId("7:abc", out _, out _));
    }

}

/// <summary>Менеджер: аудит, свои строки с повторной проверкой, перепроверка устаревших, комментирование по последнему аудиту.</summary>
public sealed class HostsManagerToolsTests : IDisposable
{
    readonly TempDir _t = new("-hosts-tools");
    readonly string _hosts;

    public HostsManagerToolsTests()
    {
        Directory.CreateDirectory(_t.Sub("etc"));
        _hosts = _t.Sub("etc\\hosts");
    }

    public void Dispose() => _t.Dispose();

    sealed class Probe(HashSet<string> ok, HashSet<string>? badCert = null) : IEndpointProbe
    {
        public HashSet<string> Ok { get; } = ok;
        public Task<EndpointCheck> CheckAsync(string host, IPAddress ip, CancellationToken ct = default) =>
            Task.FromResult(Ok.Contains($"{host}@{ip}") ? new EndpointCheck(EndpointOutcome.Ok, 0.1, "HTTP 200")
                : badCert?.Contains($"{host}@{ip}") == true ? new EndpointCheck(EndpointOutcome.BadCertificate, 0.1, "чужой сертификат")
                : new EndpointCheck(EndpointOutcome.Timeout, 5, "не отвечает"));
    }

    sealed class Dns(Dictionary<string, string> map) : IHostsDns
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) =>
            Task.FromResult(map.TryGetValue(host, out var ip) ? new[] { IPAddress.Parse(ip) } : []);
    }

    HostsManager Manager(Probe probe, Dictionary<string, string>? dns = null) =>
        new(Make.Paths(_t), _hosts, probe, new Dns(dns ?? []), null, () => []);

    // Устаревшие строки GitHub и вредная заглушка — аудит находит их как «Сломает» (ТЗ 12, «Готово, когда»).
    const string Guide = "# my hosts\r\n127.0.0.1 localhost\r\n" +
        "140.82.112.4 github.com\r\n" +             // старый адрес: не отвечает, а DNS работает
        "0.0.0.0 discord.com\r\n" +                 // заглушка на рабочий сайт
        "0.0.0.0 tracker.example.com\r\n" +         // заглушка на сайт, который и так не открывался
        "9.9.9.9 phish.example.com\r\n" +           // чужой сертификат
        "# 1.1.1.1 off.example.com\r\n";

    [Fact]
    public async Task Audit_FindsStaleGithubLine_AsBreaks_AndIgnoresOurOwn()
    {
        File.WriteAllText(_hosts, Guide, Encoding.Latin1);
        var m = Manager(new Probe(["github.com@140.82.121.3", "discord.com@162.159.138.232", "raw.githubusercontent.com@146.75.22.132"], ["phish.example.com@9.9.9.9"]),
            new() { ["github.com"] = "140.82.121.3", ["discord.com"] = "162.159.138.232", ["tracker.example.com"] = "3.3.3.30" });
        // Свою строку и блок белого списка аудит не трогает.
        await m.SetUserAsync([new("raw.githubusercontent.com", "146.75.22.132")]);
        Assert.Contains("# dpishell-user", File.ReadAllText(_hosts));
        var report = await m.AuditAsync();

        var verdicts = report.Rows.ToDictionary(r => r.Host, r => r.Verdict);
        Assert.Equal(HostsTryVerdict.Breaks, verdicts["github.com"]);
        Assert.Equal(HostsTryVerdict.Breaks, verdicts["discord.com"]);
        Assert.Equal(HostsTryVerdict.Blocks, verdicts["tracker.example.com"]);
        Assert.Equal(HostsTryVerdict.Dangerous, verdicts["phish.example.com"]);
        Assert.Equal(4, report.Rows.Count);   // localhost и комментарий не проверяются
        Assert.Equal([3, 4, 6], report.Rows.Where(r => r.Problem).Select(r => r.Line).Order());
        Assert.All(report.Rows, r => Assert.Matches(@"^\d+:[0-9a-f]{12}$", r.LineId!));
        Assert.Equal(3, report.ProblemIds.Count);
    }

    [Fact]
    public async Task Audit_HugeFile_IsCapped_RealAddressesFirst()
    {
        var sb = new StringBuilder("127.0.0.1 localhost\r\n");
        for (int i = 0; i < 700; i++) sb.Append($"0.0.0.0 ad{i}.example.com\r\n");
        sb.Append("3.3.3.3 late.example.com\r\n");
        File.WriteAllText(_hosts, sb.ToString(), Encoding.Latin1);
        var report = await Manager(new Probe(["late.example.com@3.3.3.3"])).AuditAsync();
        Assert.Equal(HostsPaste.MaxEntries, report.Rows.Count);
        Assert.True(report.Truncated);
        Assert.Equal(701, report.Total);
        Assert.Contains(report.Rows, r => r.Host == "late.example.com");   // настоящий адрес проверен, хоть и в конце файла
    }

    [Fact]
    public async Task SetUser_RechecksNewOnly_RejectsBadOnes_KeepsOldUntouched()
    {
        File.WriteAllText(_hosts, "127.0.0.1 localhost\r\n", Encoding.Latin1);
        var probe = new Probe(["github.com@140.82.121.3"], ["fake.example.com@8.8.8.8"]);
        var m = Manager(probe);

        var r = await m.SetUserAsync([new("github.com", "140.82.121.3"), new("fake.example.com", "8.8.8.8"), new("dead.example.com", "7.7.7.7"),
            new("lan.example.com", "192.168.1.5"), new("a b.example.com", "1.2.3.4")]);
        Assert.True(r.Changed);
        Assert.Equal([new("github.com", "140.82.121.3")], r.Added);
        Assert.Equal(4, r.Rejected.Count);
        Assert.Contains(r.Rejected, x => x.Host == "fake.example.com" && x.Reason.Contains("не этому сайту"));
        Assert.Contains(r.Rejected, x => x.Host == "dead.example.com" && x.Reason.Contains("не отвечает"));
        Assert.DoesNotContain("fake.example.com", File.ReadAllText(_hosts));

        // Адрес github «умер», но строка уже записана — повторная запись её не проверяет и не снимает.
        probe.Ok.Clear();
        var again = await m.SetUserAsync([new("github.com", "140.82.121.3")]);
        Assert.False(again.Changed);
        Assert.Empty(again.Rejected);
        Assert.Contains("140.82.121.3 github.com", File.ReadAllText(_hosts));
    }

    [Fact]
    public async Task Recheck_StaleUserLine_IsReportedOnce_NeverRemoved_AndShownInStatus()
    {
        File.WriteAllText(_hosts, "127.0.0.1 localhost\r\n", Encoding.Latin1);
        var probe = new Probe(["github.com@140.82.121.3"]);
        var m = Manager(probe);
        await m.SetUserAsync([new("github.com", "140.82.121.3")]);

        var ok = await m.RecheckAsync();
        Assert.Empty(ok.StaleUser!);
        Assert.Empty(m.Status().UserStale!);

        probe.Ok.Clear();   // адрес перестал работать
        var first = await m.RecheckAsync();
        Assert.Equal([new("github.com", "140.82.121.3")], first.StaleUser);
        Assert.Equal([new("github.com", "140.82.121.3")], first.NewStaleUser);
        Assert.Contains("140.82.121.3 github.com", File.ReadAllText(_hosts));   // строку выбрал человек — сами не убираем
        Assert.Equal([new("github.com", "140.82.121.3")], m.Status().UserStale);

        var second = await m.RecheckAsync();
        Assert.Single(second.StaleUser!);
        Assert.Empty(second.NewStaleUser!);   // о том же — второй раз не сообщаем

        // Строку убрали — устаревшая отметка уходит вместе с ней.
        await m.SetUserAsync([]);
        Assert.Empty(m.Status().UserStale!);
    }

    [Fact]
    public async Task Recheck_WithoutWhitelistBlock_StillChecksUserLines()
    {
        File.WriteAllText(_hosts, "127.0.0.1 localhost\r\n", Encoding.Latin1);
        var m = Manager(new Probe([]));
        var r = await m.RecheckAsync();
        Assert.False(r.Write.Changed);
        Assert.Empty(r.StaleUser!);
    }

    [Fact]
    public async Task Comment_OnlyAllowedIds_AndRestoreAll_ReturnsEverything()
    {
        File.WriteAllText(_hosts, Guide, Encoding.Latin1);
        var m = Manager(new Probe(["github.com@140.82.121.3"]), new() { ["github.com"] = "140.82.121.3" });
        var report = await m.AuditAsync();
        var github = report.Rows.First(r => r.Host == "github.com");
        var other = report.Rows.First(r => r.Host == "tracker.example.com");   // «Блокирует» — не мешающая, помощник не даст её тронуть

        var res = m.Comment([github.LineId!, other.LineId!], report.ProblemIds.ToHashSet());
        Assert.True(res.Changed);
        Assert.Single(res.Commented);
        Assert.Single(res.Skipped);
        Assert.Contains("не из последней проверки", res.Skipped[0]);
        var text = File.ReadAllText(_hosts, Encoding.Latin1);
        Assert.Contains($"# DpiShell off {DateTime.Now:yyyy-MM-dd}: 140.82.112.4 github.com", text);
        Assert.Contains("\r\n0.0.0.0 tracker.example.com\r\n", text);
        Assert.Single(m.Status().OffLines!);

        var back = m.RestoreAll();
        Assert.True(back.Changed);
        Assert.Equal(Guide, File.ReadAllText(_hosts, Encoding.Latin1));
        Assert.Empty(m.Status().OffLines!);
    }

    [Fact]
    public async Task Try_DoesNotChangeHosts_AndLimitsTo500()
    {
        File.WriteAllText(_hosts, Guide, Encoding.Latin1);
        var before = File.ReadAllBytes(_hosts);
        var many = Enumerable.Range(0, 520).Select(i => new HostsEntry($"h{i}.example.com", "0.0.0.0")).ToList();
        var report = await Manager(new Probe([])).TryAsync(many);
        Assert.Equal(500, report.Rows.Count);
        Assert.True(report.Truncated);
        Assert.Equal(520, report.Total);
        Assert.Equal(before, File.ReadAllBytes(_hosts));
        Assert.Empty(Directory.Exists(_t.Sub("root\\hosts")) ? Directory.GetFiles(_t.Sub("root\\hosts"), "*.bak") : []);   // даже копию не делали
    }
}
