using System.Net;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Выбор DNS: описание варианта, копирование адресов и «Сравнить» — без сети и без буфера обмена.</summary>
public sealed class DnsDetailTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    MainViewModel Model() => TestWindow.Model(new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data)));

    static string Info(DnsOption o, string label) => o.Info.SingleOrDefault(l => l.Label == label)?.Value ?? "";

    [Fact]
    public void PcPresets_Described_FromPresets()
    {
        var pc = Model().PcDns;
        var google = pc.Options.Single(o => o.Id == "google");
        Assert.Equal("8.8.8.8, 8.8.4.4", Info(google, "IPv4"));
        Assert.Equal("2001:4860:4860::8888, 2001:4860:4860::8844", Info(google, "IPv6"));
        Assert.Equal("https://dns.google/dns-query", Info(google, "Адрес защищённого DNS"));
        Assert.Equal("Google (США)", Info(google, "Оператор"));
        Assert.Equal("рекомендуем", google.Note);

        var yandex = pc.Options.Single(o => o.Id == "yandex");
        Assert.Equal("российский: может применять реестр запрещённых сайтов", yandex.Note);
        Assert.Equal("Яндекс (Россия)", Info(yandex, "Оператор"));
        Assert.Contains("вредоносные", pc.Options.Single(o => o.Id == "quad9").Note);

        // «Как у провайдера» — описания нет, остаётся строка-сводка; копировать нечего.
        pc.Selected = pc.Options.Single(o => o.Id == "auto");
        Assert.Empty(pc.SelectedInfo);
        Assert.True(pc.HasSelectedDetail);
        Assert.False(pc.CopyIpsCmd.CanExecute(null));
        Assert.False(pc.CopyDohCmd.CanExecute(null));
        pc.Selected = google;
        Assert.False(pc.HasSelectedDetail);
        Assert.Equal(4, pc.SelectedInfo.Count);
        Assert.Equal("рекомендуем", pc.SelectedNote);
    }

    [Fact]
    public void Copy_AddressesOnePerLine_AndDohTemplate()
    {
        var pc = Model().PcDns;
        var copied = new List<string>();
        pc.CopyText = copied.Add;
        pc.Selected = pc.Options.Single(o => o.Id == "cloudflare");
        pc.CopyIpsCmd.Execute(null);
        Assert.Equal(string.Join(Environment.NewLine, "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"), copied[0]);
        Assert.Equal("Адреса — в буфере обмена", pc.Copied);
        pc.CopyDohCmd.Execute(null);
        Assert.Equal("https://cloudflare-dns.com/dns-query", copied[1]);
        pc.Selected = pc.Options.Single(o => o.Id == "google");
        Assert.False(pc.HasCopied);   // подсказка — про прежний вариант, при смене убирается

        // Свой вариант: что ввёл человек.
        pc.NewName = "AdGuard"; pc.NewIps = "94.140.14.14, 94.140.15.15"; pc.NewDoh = "https://dns.adguard-dns.com/dns-query";
        pc.AddCustom();
        Assert.Equal("94.140.14.14, 94.140.15.15", Info(pc.Selected!, "IPv4"));
        pc.CopyDohCmd.Execute(null);
        Assert.Equal("https://dns.adguard-dns.com/dns-query", copied[^1]);

        // Буфер занят другой программой — не падаем.
        pc.CopyText = _ => throw new System.Runtime.InteropServices.COMException("занят");
        pc.CopyIpsCmd.Execute(null);
        Assert.Equal("Буфер обмена занят — повторите", pc.Copied);
    }

    [Fact]
    public async Task Compare_MeasuresEachDoh_ShowsMsNextToTitle()
    {
        var pc = Model().PcDns;
        var asked = new List<string>();
        pc.Measure = (doh, _) =>
        {
            lock (asked) asked.Add(doh);
            return Task.FromResult<double?>(doh.Contains("quad9") ? null : doh.Contains("google") ? 38.4 : 51);
        };
        pc.Selected = pc.Options.Single(o => o.Id == "google");
        Assert.True(pc.CompareCmd.CanExecute(null));
        await pc.CompareAsync();
        Assert.Equal(4, asked.Count);   // у «как у провайдера» шаблона нет
        Assert.Equal("38 мс", pc.Options.Single(o => o.Id == "google").Ping);
        Assert.Equal("нет ответа", pc.Options.Single(o => o.Id == "quad9").Ping);
        Assert.Equal("", pc.Options.Single(o => o.Id == "auto").Ping);
        Assert.Equal("google", pc.Selected!.Id);   // выбор не сбился
        Assert.Equal("38 мс", Info(pc.Selected, "Ответ с этого ПК"));
        Assert.Equal("Сравнить скорость", pc.CompareText);
        // Итог виден строкой, не только в закрытом списке: первым — самый быстрый.
        Assert.True(pc.HasCompareSummary);
        Assert.StartsWith("Быстрее всех: " + pc.Options.Single(o => o.Id == "google").Title + " 38 мс", pc.CompareSummary);
        Assert.Contains("Нет ответа: " + pc.Options.Single(o => o.Id == "quad9").Title, pc.CompareSummary);
    }

    [Fact]
    public void DohPing_Query_IsRfc8484Wire()
    {
        // id 0, RD, 1 вопрос; example.com A IN — base64url без «=».
        var q = DohPing.Query("example.com");
        var bytes = Convert.FromBase64String(q.Replace('-', '+').Replace('_', '/') + new string('=', (4 - q.Length % 4) % 4));
        Assert.Equal(new byte[] { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1 }, bytes);
        Assert.DoesNotContain('=', q);
    }

    sealed class FakeDoh(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<string> Urls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(answer(request));
        }
    }

    [Fact]
    public async Task DohPing_Measure_DnsAnswerOnly()
    {
        var dns = new FakeDoh(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0]) });
        var ms = await DohPing.MeasureAsync("https://dns.example/dns-query", dns);
        Assert.NotNull(ms);
        Assert.Equal(2, dns.Urls.Count);   // меряется второй запрос
        Assert.StartsWith("https://dns.example/dns-query?dns=AAABAAABAAAAAAAAB2V4YW1wbGUDY29tAAABAAE", dns.Urls[0]);

        // Страница-заглушка вместо DNS-ответа и ошибка сервера — «нет ответа».
        var stub = new FakeDoh(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>blocked</html>") });
        Assert.Null(await DohPing.MeasureAsync("https://dns.example/dns-query", stub));
        var down = new FakeDoh(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        Assert.Null(await DohPing.MeasureAsync("https://dns.example/dns-query", down));
        var broken = new FakeDoh(_ => throw new HttpRequestException("нет соединения"));
        Assert.Null(await DohPing.MeasureAsync("https://dns.example/dns-query", broken));
    }
}
