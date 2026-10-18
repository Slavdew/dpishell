using System.Security.Authentication;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class HttpProbeTests : IAsyncLifetime
{
    RawHttpServer _srv = null!;
    static readonly SslProtocols Tls = SslProtocols.Tls13; // на http не влияет

    static readonly HttpProbe Probe = new() { ConnectTimeout = TimeSpan.FromSeconds(10), ReadTimeout = TimeSpan.FromSeconds(5), Attempts = 1 };

    public Task InitializeAsync()
    {
        _srv = new RawHttpServer(Handle);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _srv.DisposeAsync();

    async Task Handle(string path, Stream s, CancellationToken ct)
    {
        switch (path)
        {
            case "/big": await RawHttpServer.RespondAsync(s, 200, new byte[30_000]); break;
            case "/small": await RawHttpServer.RespondAsync(s, 200, new byte[100]); break;
            case "/chunked":
                await RawHttpServer.WriteHeadAsync(s, 200, null, "Transfer-Encoding: chunked");
                await s.WriteAsync("10\r\n0123456789abcdef\r\n0\r\n\r\n"u8.ToArray(), ct);
                break;
            case "/truncated": // обещаем 100 КБ, отдаём 5 КБ и закрываем соединение
                await RawHttpServer.WriteHeadAsync(s, 200, 100_000);
                await s.WriteAsync(new byte[5_000], ct);
                await s.FlushAsync(ct);
                break;
            case "/stall": // обещаем 100 КБ, отдаём 5 КБ и молчим (как ТСПУ после ~16 КБ)
                await RawHttpServer.WriteHeadAsync(s, 200, 100_000);
                await s.WriteAsync(new byte[5_000], ct);
                await s.FlushAsync(ct);
                await Task.Delay(Timeout.Infinite, ct);
                break;
            case "/silent": await Task.Delay(Timeout.Infinite, ct); break;
            case "/redir-rel": await RawHttpServer.RespondAsync(s, 302, [], "Location: /big"); break;
            case "/redir-abs": await RawHttpServer.RespondAsync(s, 301, [], $"Location: {_srv.Url("/big")}"); break;
            case "/redir-foreign": await RawHttpServer.RespondAsync(s, 302, [], "Location: http://warning.rt.ru/?id=1"); break;
            case "/redir-none": await RawHttpServer.RespondAsync(s, 302, []); break;
            case "/500": await RawHttpServer.RespondAsync(s, 500, new byte[30_000]); break;
            case "/503": await RawHttpServer.RespondAsync(s, 503, []); break;
            case "/400": await RawHttpServer.RespondAsync(s, 400, new byte[30_000]); break;
            case "/404": await RawHttpServer.RespondAsync(s, 404, new byte[30_000]); break;
            case "/403": await RawHttpServer.RespondAsync(s, 403, new byte[30_000]); break;
            case "/451": await RawHttpServer.RespondAsync(s, 451, new byte[30_000]); break;
            case "/redir-small": await RawHttpServer.RespondAsync(s, 301, [], "Location: /small"); break;
            case "/redir-foreign-2": await RawHttpServer.RespondAsync(s, 301, [], "Location: /redir-foreign"); break;
            case "/redir-451": await RawHttpServer.RespondAsync(s, 302, [], "Location: /451"); break;
            case var p when p.StartsWith("/hop/"): // /hop/N: N переходов до /big
                var left = int.Parse(p[5..]);
                await RawHttpServer.RespondAsync(s, 301, [], "Location: " + (left <= 1 ? "/big" : $"/hop/{left - 1}"));
                break;
            default: await RawHttpServer.RespondAsync(s, 404, []); break;
        }
    }

    Task<ProbeResult> Once(string path, int min = 0, bool handshake = false, HttpProbe? p = null) =>
        (p ?? Probe).OnceAsync(new Target(_srv.Url(path), min, handshake), Tls);

    [Fact]
    public async Task FullBody_AboveMinBytes_Ok()
    {
        var r = await Once("/big", 20_480);
        Assert.Equal(ProbeOutcome.Ok, r.Outcome);
        Assert.Equal(30_000, r.Bytes);
        Assert.Equal(Tls, r.Tls);
    }

    [Fact]
    public async Task FullBody_BelowMinBytes_Truncated()
    {
        var r = await Once("/small", 20_480);
        Assert.Equal(ProbeOutcome.Truncated, r.Outcome);
        Assert.Equal(100, r.Bytes);
    }

    [Fact]
    public async Task Chunked_NoContentLength_Ok()
    {
        var r = await Once("/chunked", 1);
        Assert.Equal(ProbeOutcome.Ok, r.Outcome);
        Assert.Equal(16, r.Bytes);
    }

    [Fact]
    public async Task ContentLengthLie_ThenClose_Truncated()
    {
        var r = await Once("/truncated");
        Assert.Equal(ProbeOutcome.Truncated, r.Outcome);
        Assert.Equal(5_000, r.Bytes);
    }

    [Fact]
    public async Task ContentLengthLie_ThenSilence_Truncated()
    {
        var p = new HttpProbe { ConnectTimeout = TimeSpan.FromSeconds(1), ReadTimeout = TimeSpan.FromMilliseconds(700), Attempts = 1 };
        var r = await Once("/stall", p: p);
        Assert.Equal(ProbeOutcome.Truncated, r.Outcome);
        Assert.Equal(5_000, r.Bytes);
    }

    [Fact]
    public async Task MaxBytes_StopsReadingAndCountsAsOk()
    {
        var p = new HttpProbe { ConnectTimeout = TimeSpan.FromSeconds(10), ReadTimeout = TimeSpan.FromSeconds(5), Attempts = 1, MaxBytes = 1000 };
        var r = await Once("/big", p: p);
        Assert.Equal(ProbeOutcome.Ok, r.Outcome);
        Assert.True(r.Bytes >= 1000);
    }

    [Theory]
    [InlineData("/redir-rel")]
    [InlineData("/redir-abs")]
    public async Task RedirectSameSite_Ok(string path) => Assert.Equal(ProbeOutcome.Ok, (await Once(path, 20_480)).Outcome);

    [Fact]
    public async Task RedirectForeign_ForeignRedirect()
    {
        var r = await Once("/redir-foreign");
        Assert.Equal(ProbeOutcome.ForeignRedirect, r.Outcome);
        Assert.Contains("warning.rt.ru", r.Detail);
    }

    [Fact]
    public async Task RedirectWithoutLocation_ForeignRedirect() =>
        Assert.Equal(ProbeOutcome.ForeignRedirect, (await Once("/redir-none")).Outcome);

    [Theory]
    [InlineData("/500")]
    [InlineData("/503")]
    [InlineData("/400")]
    public async Task ServerErrorsAnd400_BadStatus(string path) =>
        Assert.Equal(ProbeOutcome.BadStatus, (await Once(path, 20_480)).Outcome);

    [Fact]
    public async Task Redirect_FollowedToFinalPage_DetailHasChain()
    {
        var r = await Once("/redir-abs", 20_480);
        Assert.Equal(ProbeOutcome.Ok, r.Outcome);
        Assert.Equal(30_000, r.Bytes);   // судим по конечному телу
        Assert.Equal("301 → /big → 200, 29 КБ", r.Detail);
    }

    // Было: 301 сразу Ok в обход MinBytes. Теперь — по конечной странице.
    [Fact]
    public async Task Redirect_ToSmallBody_Truncated()
    {
        var r = await Once("/redir-small", 20_480);
        Assert.Equal(ProbeOutcome.Truncated, r.Outcome);
        Assert.Equal(100, r.Bytes);
        Assert.StartsWith("301 → /small → 200", r.Detail);
    }

    [Fact]
    public async Task Redirect_FiveHopsOk_SixFail()
    {
        Assert.Equal(ProbeOutcome.Ok, (await Once("/hop/5", 20_480)).Outcome);
        var r = await Once("/hop/6", 20_480);
        Assert.Equal(ProbeOutcome.BadStatus, r.Outcome);
        Assert.Contains("больше 5 переходов", r.Detail);
    }

    [Fact]
    public async Task Redirect_ThenForeign_ForeignRedirect()
    {
        var r = await Once("/redir-foreign-2");
        Assert.Equal(ProbeOutcome.ForeignRedirect, r.Outcome);
        Assert.Contains("warning.rt.ru", r.Detail);
    }

    [Theory]
    [InlineData("/451")]
    [InlineData("/redir-451")]
    public async Task Status451_Blocked(string path)
    {
        var r = await Once(path);
        Assert.Equal(ProbeOutcome.Blocked, r.Outcome);
        Assert.Contains("451", r.Detail);
    }

    // Было: 403/404 с телом — «сайт открылся». У целей с телом это провал с понятным текстом.
    [Theory]
    [InlineData("/403", "403: доступ запрещён")]
    [InlineData("/404", "404: страницы нет")]
    public async Task ForbiddenAndNotFound_WithBodyTarget_BadStatus(string path, string detail)
    {
        var r = await Once(path);
        Assert.Equal(ProbeOutcome.BadStatus, r.Outcome);
        Assert.Equal(detail, r.Detail);
    }

    [Theory]
    [InlineData("/500")]
    [InlineData("/400")]
    [InlineData("/404")]
    [InlineData("/403")]
    [InlineData("/redir-foreign")]
    [InlineData("/small")]
    public async Task HandshakeOnly_AnyStatus_Ok(string path)
    {
        var r = await Once(path, 20_480, handshake: true);
        Assert.Equal(ProbeOutcome.Ok, r.Outcome);
        Assert.Equal(0, r.Bytes);
    }

    [Fact]
    public async Task ClosedPort_ConnectFailed()
    {
        var port = RawHttpServer.ClosedPort();
        var r = await Probe.OnceAsync(new Target($"http://127.0.0.1:{port}/"), Tls);
        Assert.Equal(ProbeOutcome.ConnectFailed, r.Outcome);
    }

    [Fact]
    public async Task SilentServer_Timeout()
    {
        var p = new HttpProbe { ConnectTimeout = TimeSpan.FromMilliseconds(500), ReadTimeout = TimeSpan.FromMilliseconds(500), Attempts = 1 };
        var r = await Once("/silent", p: p);
        Assert.Equal(ProbeOutcome.Timeout, r.Outcome);
        Assert.Equal(0, r.Bytes);
        Assert.InRange(r.Seconds, 0.8, 5);
    }

    [Fact]
    public async Task ExternalCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Probe.OnceAsync(new Target(_srv.Url("/silent")), Tls, cts.Token));
    }

    [Fact]
    public async Task CheckAsync_OneResultPerTlsVersion_RetriesFailures()
    {
        var p = new HttpProbe { ConnectTimeout = TimeSpan.FromSeconds(10), ReadTimeout = TimeSpan.FromSeconds(5), Attempts = 2 };
        var before = _srv.Requests;
        var r = await p.CheckAsync(new Target(_srv.Url("/500")), [SslProtocols.Tls13, SslProtocols.Tls12]);
        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.Equal(ProbeOutcome.BadStatus, x.Outcome));
        Assert.Equal(before + 4, _srv.Requests);

        before = _srv.Requests;
        var ok = await p.CheckAsync(new Target(_srv.Url("/big")), [SslProtocols.Tls12]);
        Assert.True(Assert.Single(ok).Ok);
        Assert.Equal(before + 1, _srv.Requests); // успех — без повторов
    }

    [Theory]
    [InlineData("youtube.com", "www.youtube.com", true)]
    [InlineData("WWW.YouTube.com", "m.youtube.COM", true)]
    [InlineData("youtube.com", "warning.rt.ru", false)]
    [InlineData("discord.com", "discord.gg", false)]
    [InlineData("localhost", "localhost", true)]
    public void SameSite_Basic(string a, string b, bool same) => Assert.Equal(same, HttpProbe.SameSite(a, b));

    // БАГ: «сайт» = два последних метки. Для доменов под публичными суффиксами (co.uk, com.ru, msk.ru)
    // и для IP-адресов разные хосты считаются одним сайтом — заглушка провайдера на таком хосте пройдёт как Ok.
    [Theory]
    [InlineData("bbc.co.uk", "blocked.co.uk")]
    [InlineData("site.com.ru", "stub.com.ru")]
    [InlineData("10.0.0.1", "192.168.0.1")]
    public void Regression_SameSite_PublicSuffixAndIp_ShouldBeDifferent(string a, string b) =>
        Assert.False(HttpProbe.SameSite(a, b));
}
