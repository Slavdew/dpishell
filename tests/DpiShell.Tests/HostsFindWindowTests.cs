using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>«Подобрать адрес» в «Примерке» (п. 97): адрес из гайда не помог — окно ищет рабочий через DNS, помощник примеряет найденное.</summary>
public sealed class HostsFindWindowTests
{
    static HostsTryRow Row(string host, string ip, HostsTryVerdict v, DnsState dns) =>
        new(host, ip, v, null, null, DnsMatch.Differs, dns, "");

    [Fact]
    public async Task Find_OnlyForNotWorkingSites_FoundAddressTriedAndWritable()
    {
        var f = new HelperFixture();
        await using var _ = f;
        var link = await f.LinkAsync();
        var asked = new List<IReadOnlyList<string>>();
        var vm = new HostsViewModel(link)
        {
            FindAddresses = (hosts, _) =>
            {
                asked.Add(hosts);
                return Task.FromResult<IReadOnlyList<HostsEntry>>([new("web.telegram.org", "149.154.167.99")]);
            },
        };
        f.Backend.TryRowsFor = e => [.. e.Select(x => Row(x.Host, x.Ip, HostsTryVerdict.Helps, DnsState.Fails))];
        vm.ShowTry(new HostsTryReport(
        [
            Row("web.telegram.org", "1.2.3.4", HostsTryVerdict.Useless, DnsState.Fails),   // не открывается и с адресом из гайда — искать
            Row("github.com", "0.0.0.0", HostsTryVerdict.Breaks, DnsState.Works),          // заглушка — подобрать адрес вместо неё
            Row("discord.com", "5.6.7.8", HostsTryVerdict.NotNeeded, DnsState.Works),
        ], 0, false, 3, DateTime.Now));
        Assert.True(vm.CanFind);
        Assert.True(vm.FindCmd.CanExecute(null));

        await vm.FindAsync();
        Assert.Equal(["github.com", "web.telegram.org"], Assert.Single(asked).Order());   // заглушка — всегда, даже если сайт открывается; discord.com — нет
        Assert.Contains(f.Backend.Calls, c => c.StartsWith("hosts_try ") && c.Contains("149.154.167.99"));
        var found = Assert.Single(vm.TryRows, r => r.Ip == "149.154.167.99");
        Assert.True(found.CanSelect && found.Selected);   // «Поможет» — отмечена для записи
        Assert.Equal(4, vm.TryRows.Count);   // прежние строки остались
        Assert.Contains(L.T("hosts.find.found", new { n = 1 }), vm.TrySummary);
        Assert.True(vm.CanFind);   // для github.com не нашлось — можно искать ещё; для web.telegram.org рабочая строка уже есть
    }

    static EndpointCheck Ok => new(EndpointOutcome.Ok, 0.1, "");

    [Fact]
    public async Task Stubs_ReplacedByFoundAddresses_RightAfterTry_EvenIfSiteOpens()
    {
        var f = new HelperFixture();
        await using var _ = f;
        var link = await f.LinkAsync();
        var asked = new List<IReadOnlyList<string>>();
        var vm = new HostsViewModel(link)
        {
            FindAddresses = (hosts, _) =>
            {
                asked.Add(hosts);
                return Task.FromResult<IReadOnlyList<HostsEntry>>([.. hosts.Where(h => h != "nope.example").Select(h => new HostsEntry(h, "149.154.167.99"))]);
            },
        };
        // Заглушки: одна для сайта, который через DNS и так открывается, другая — нет; найденный рабочий адрес помощник видит как «Поможет» / «Не нужна».
        f.Backend.TryRowsFor = e => [.. e.Select(x => x.Ip == "0.0.0.0"
            ? Row(x.Host, x.Ip, x.Host == "web.telegram.org" ? HostsTryVerdict.Blocks : HostsTryVerdict.Breaks, x.Host == "web.telegram.org" ? DnsState.Fails : DnsState.Works)
            : new HostsTryRow(x.Host, x.Ip, x.Host == "web.telegram.org" ? HostsTryVerdict.Helps : HostsTryVerdict.NotNeeded, Ok, null, DnsMatch.Differs,
                x.Host == "web.telegram.org" ? DnsState.Fails : DnsState.Works, ""))];
        vm.PasteText = "0.0.0.0 web.telegram.org\n0.0.0.0 cdn.discordapp.com\n0.0.0.0 nope.example";
        await vm.TryAsync();

        Assert.Equal(["cdn.discordapp.com", "nope.example", "web.telegram.org"], Assert.Single(asked).Order());   // сам, без кнопки
        Assert.DoesNotContain(vm.TryRows, r => r.Ip == "0.0.0.0" && r.Host != "nope.example");   // заглушки заменены
        Assert.All(vm.TryRows.Where(r => r.Ip == "149.154.167.99"), r => Assert.True(r.CanSelect && r.Selected));   // и «Не нужна» — записать можно
        Assert.Equal(2, vm.TryRows.Count(r => r.Ip == "149.154.167.99"));
        Assert.Single(vm.TryRows, r => r.Host == "nope.example" && r.Ip == "0.0.0.0");   // не нашлось — заглушка осталась
        Assert.Contains(L.T("hosts.find.missing", new { list = "nope.example" }), vm.TrySummary);
        Assert.Equal(L.T("hosts.write_selected_n", new { n = 2 }), vm.WriteSelectedText);
    }

    [Fact]
    public async Task LongPaste_TriedInBatches_AllRowsInOneTable()
    {
        var f = new HelperFixture();
        await using var _ = f;
        var link = await f.LinkAsync();
        var vm = new HostsViewModel(link) { FindAddresses = (_, _) => Task.FromResult<IReadOnlyList<HostsEntry>>([]) };
        f.Backend.TryRowsFor = e => [.. e.Select(x => Row(x.Host, x.Ip, HostsTryVerdict.Useless, DnsState.Fails))];
        // Больше одной команды помощника (500): окно делит само, человеку не нужно искать 501-ю строку.
        vm.PasteText = string.Join("\n", Enumerable.Range(1, 1203).Select(i => $"203.0.113.{i % 250 + 1} s{i}.example.com"));
        await vm.TryAsync();
        var calls = f.Backend.Calls.Where(c => c.StartsWith("hosts_try ")).ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c => Assert.True(c.Split(',').Length <= HostsPaste.MaxBatch));
        Assert.Equal(1203, vm.TryRows.Count);
        Assert.Contains("1203", vm.TrySummary);
    }

    [Fact]
    public async Task Blocklist_NoAutoFind_SaysWhy()
    {
        var f = new HelperFixture();
        await using var _ = f;
        var link = await f.LinkAsync();
        var asked = 0;
        var vm = new HostsViewModel(link) { FindAddresses = (_, _) => { asked++; return Task.FromResult<IReadOnlyList<HostsEntry>>([]); } };
        f.Backend.TryRowsFor = e => [.. e.Select(x => Row(x.Host, x.Ip, HostsTryVerdict.Blocks, DnsState.Fails))];
        vm.PasteText = string.Join("\n", Enumerable.Range(1, 800).Select(i => $"0.0.0.0 ad{i}.example.com"));
        await vm.TryAsync();
        Assert.Equal(0, asked);
        Assert.EndsWith(L.T("hosts.find.blocklist"), vm.TrySummary);
        Assert.Equal(800, vm.TryRows.Count);
        // Короткий гайд с заглушками — подбор идёт, как раньше.
        Assert.False(HostsViewModel.LooksLikeBlocklist(HostsPaste.Parse("0.0.0.0 web.telegram.org\n0.0.0.0 cdn.discordapp.com")));
    }

    [Fact]
    public async Task Find_NothingFound_SaysSo_NoTry()
    {
        var f = new HelperFixture();
        await using var _ = f;
        var link = await f.LinkAsync();
        var vm = new HostsViewModel(link) { FindAddresses = (_, _) => Task.FromResult<IReadOnlyList<HostsEntry>>([]) };
        vm.ShowTry(new HostsTryReport([Row("web.telegram.org", "1.2.3.4", HostsTryVerdict.Dangerous, DnsState.Fails)], 0, false, 1, DateTime.Now));
        await vm.FindAsync();
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("hosts_try "));
        Assert.EndsWith(L.T("hosts.find.none"), vm.TrySummary);
        Assert.Single(vm.TryRows);
    }
}
