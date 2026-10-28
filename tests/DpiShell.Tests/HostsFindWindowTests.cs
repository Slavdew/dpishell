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
            Row("github.com", "0.0.0.0", HostsTryVerdict.Breaks, DnsState.Works),          // через DNS и так открывается — искать незачем
            Row("discord.com", "5.6.7.8", HostsTryVerdict.NotNeeded, DnsState.Works),
        ], 0, false, 3, DateTime.Now));
        Assert.True(vm.CanFind);
        Assert.True(vm.FindCmd.CanExecute(null));

        await vm.FindAsync();
        Assert.Equal(["web.telegram.org"], Assert.Single(asked));
        Assert.Contains(f.Backend.Calls, c => c.StartsWith("hosts_try ") && c.Contains("149.154.167.99"));
        var found = Assert.Single(vm.TryRows, r => r.Ip == "149.154.167.99");
        Assert.True(found.CanSelect && found.Selected);   // «Поможет» — отмечена для записи
        Assert.Equal(4, vm.TryRows.Count);   // прежние строки остались
        Assert.Equal(L.T("hosts.find.found", new { n = 1 }), vm.TrySummary);
        Assert.False(vm.CanFind);   // для сайта уже есть рабочая строка
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
        Assert.Equal(L.T("hosts.find.none"), vm.TrySummary);
        Assert.Single(vm.TryRows);
    }
}
