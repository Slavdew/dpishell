using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class StrategyNamingTests
{
    [Theory]
    [InlineData("--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern={fake}/tls.bin", "fake+multisplit · ts · ×8")]
    [InlineData("--dpi-desync=fake,fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00", "fake+fakedsplit · ts · ×6")]
    [InlineData("--dpi-desync=hostfakesplit --dpi-desync-repeats=4 --dpi-desync-fooling=ts,md5sig --dpi-desync-hostfakesplit-mod=host=ozon.ru", "hostfakesplit · ts+md5sig · ×4")]
    [InlineData("--dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=11 --dpi-desync-fooling=badseq", "fake+multidisorder · badseq · ×11")]
    [InlineData("--ip-id=zero --dpi-desync=multidisorder --dpi-desync-split-pos=midsld", "multidisorder")]
    public void Summary_FromRealParameters(string args, string expected) => Assert.Equal(expected, StrategyNaming.Summary(args));

    [Fact]
    public void Summary_NoDesync_Null()
    {
        Assert.Null(StrategyNaming.Summary("--wf-tcp=443"));
        Assert.Null(StrategyNaming.Summary(""));
        Assert.Null(StrategyNaming.Summary(null));
    }

    [Fact]
    public void Name_DiscordTcp_ShownWhenItDiffers()
    {
        var a = "--dpi-desync=fake --dpi-desync-fooling=ts --dpi-desync-repeats=6";
        var b = "--dpi-desync=multisplit --dpi-desync-split-pos=1";
        Assert.Equal("Новая: fake · ts · ×6", StrategyNaming.Name(new Dictionary<string, string> { ["google"] = a, ["discord_tcp"] = a, ["general"] = a }));
        Assert.Equal("Новая: google fake · ts · ×6; discord multisplit; general fake · ts · ×6",
            StrategyNaming.Name(new Dictionary<string, string> { ["google"] = a, ["discord_tcp"] = b, ["general"] = a }));
    }

    [Fact]
    public void Name_SameRoles_OneSummary()
    {
        var a = "--dpi-desync=fake,multisplit --dpi-desync-fooling=ts --dpi-desync-repeats=6";
        Assert.Equal("Новая: fake+multisplit · ts · ×6", StrategyNaming.Name(new Dictionary<string, string> { ["google"] = a, ["general"] = a }));
        Assert.Equal("Новая: fake+multisplit · ts · ×6", StrategyNaming.Name(new Dictionary<string, string> { ["general"] = a }));
    }

    [Fact]
    public void Name_DifferentRoles_BothShown()
    {
        var roles = new Dictionary<string, string>
        {
            ["google"] = "--dpi-desync=fake --dpi-desync-fooling=ts --dpi-desync-repeats=6",
            ["general"] = "--dpi-desync=multisplit --dpi-desync-repeats=2",
        };
        Assert.Equal("Новая: google fake · ts · ×6; general multisplit · ×2", StrategyNaming.Name(roles));
    }

    [Fact]
    public void Name_NoRoles_Null()
    {
        Assert.Null(StrategyNaming.Name(null));
        Assert.Null(StrategyNaming.Name(new Dictionary<string, string> { ["quic"] = "--dpi-desync=fake" }));
    }

    [Theory]
    [InlineData("Новая стратегия 1 (поиск 18.10 07:33)", true)]
    [InlineData("Найденная 2 (261016-2213)", true)]
    [InlineData("Найденная: fake + split2", false)]
    [InlineData("Моя лучшая", false)]
    [InlineData("general (ALT3)", false)]
    public void IsAutoName(string title, bool auto) => Assert.Equal(auto, StrategyNaming.IsAutoName(title));

    [Fact]
    public void DisplayTitle_AutoNameReplaced_UserNameKept_NoRolesKept()
    {
        var roles = new Dictionary<string, string> { ["general"] = "--dpi-desync=fake,multisplit --dpi-desync-fooling=ts --dpi-desync-repeats=6" };
        Assert.Equal("Новая: fake+multisplit · ts · ×6", StrategyNaming.DisplayTitle("Новая стратегия 1 (поиск 18.10 07:33)", roles));
        Assert.Equal("Моя лучшая", StrategyNaming.DisplayTitle("Моя лучшая", roles));
        Assert.Equal("Найденная 1 (261016-2213)", StrategyNaming.DisplayTitle("Найденная 1 (261016-2213)", null));
        Assert.Equal("поиск 18.10 07:33", StrategyNaming.AutoNameDate("Новая стратегия 1 (поиск 18.10 07:33)"));
        Assert.Equal("Новая стратегия 1 (поиск 18.10 07:33)", StrategyNaming.Tip("Новая стратегия 1 (поиск 18.10 07:33)", roles));
        Assert.Equal("", StrategyNaming.Tip("Моя лучшая", roles));
    }
}
