using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Обмен стратегиями: строка dpishell:1:… и запуск winws из .bat Flowseal. Строка приходит от постороннего человека,
/// а winws работает с правами администратора — поэтому тесты в основном про отказы: чужой путь, неразрешённые ключи,
/// кавычки и «&amp;», огромная строка, битый base64, «бомба» при распаковке.
/// </summary>
public sealed class StrategyExchangeTests
{
    static readonly string FakeDir = Path.Combine(Repo.Data, "fake");
    static bool Exists(string name) => File.Exists(Path.Combine(FakeDir, name));

    /// <summary>Строка обмена из произвольного JSON — как её собрал бы недобросовестный отправитель.</summary>
    static string Raw(string json, string prefix = StrategyExchange.Prefix)
    {
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.SmallestSize, true)) d.Write(Encoding.UTF8.GetBytes(json));
        return prefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static string One(string role, string args) => Raw(JsonSerializer.Serialize(new { title = "Т", roles = new Dictionary<string, string> { [role] = args } }));

    static string Reason(Func<ParsedStrategy> parse) => Assert.Throws<StrategyFormatException>(parse).Message;

    // ---------- формат dpishell:1: ----------

    [Fact]
    public void RoundTrip_EveryCatalogStrategy_KeepsTitleAndRoles()
    {
        var catalog = StrategyCatalog.Load(Repo.Strategies);
        foreach (var s in catalog.Strategies)
        {
            var text = StrategyExchange.Encode(s.Title, s.Roles);
            Assert.StartsWith("dpishell:1:", text);
            Assert.True(text.Length < StrategyExchange.MaxText, $"{s.Id}: {text.Length}");
            Assert.DoesNotContain(' ', text);
            var back = StrategyExchange.Parse(text, Exists);
            Assert.Equal(StrategyExchange.CleanTitle(s.Title), back.Title);
            Assert.Equal(s.Roles.OrderBy(r => r.Key), back.Roles.OrderBy(r => r.Key));
            Assert.False(back.FromBat);
        }
    }

    [Fact]
    public void Parse_TolerantToWhitespaceAroundString()
    {
        var text = StrategyExchange.Encode("Моя", new Dictionary<string, string> { ["general"] = "--dpi-desync=fake --dpi-desync-repeats=6" });
        Assert.Equal("Моя", StrategyExchange.Parse("  \r\n" + text + " \r\n", Exists).Title);
    }

    [Theory]
    [InlineData("", "В буфере обмена пусто")]
    [InlineData("   ", "В буфере обмена пусто")]
    [InlineData("dpishell:1:", "Строка повреждена")]
    [InlineData("dpishell:1:!!!!", "Строка повреждена")]
    [InlineData("dpishell:1:A", "Строка повреждена")]               // длина 1 — не base64
    [InlineData("dpishell:1:abc def", "Строка повреждена")]         // пробел внутри
    [InlineData("dpishell:1:YWJj+/==", "Строка повреждена")]        // обычный base64 вместо url-вида
    [InlineData("dpishell:1:AAAA", "Строка повреждена")]            // base64 есть, deflate нет
    [InlineData("dpishell:", "Строка повреждена")]
    [InlineData("dpishell:x:abc", "Строка повреждена")]
    public void BrokenString_ShortReason(string text, string reason) => Assert.Equal(reason, Reason(() => StrategyExchange.Parse(text, Exists)));

    [Fact]
    public void NewerVersion_AsksToUpdate()
    {
        var r = Reason(() => StrategyExchange.Parse("dpishell:2:abcd", Exists));
        Assert.Contains("версии 2", r);
        Assert.Contains("обновите", r);
    }

    [Fact]
    public void HugeString_RejectedBySize()
    {
        var text = "dpishell:1:" + new string('A', StrategyExchange.MaxText);
        Assert.Contains("слишком длинная", Reason(() => StrategyExchange.Parse(text, Exists)));
    }

    [Fact]
    public void DecompressionBomb_RejectedBeforeItGrows()
    {
        // ~4 МБ нулей сжимаются в несколько килобайт: строка проходит по длине, но распаковка упирается в предел.
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.SmallestSize, true)) d.Write(new byte[4 * 1024 * 1024]);
        var text = "dpishell:1:" + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(text.Length < StrategyExchange.MaxText, "для теста строка должна пройти по длине: " + text.Length);
        Assert.Contains("после распаковки", Reason(() => StrategyExchange.Parse(text, Exists)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("{\"title\":\"a\",\"roles\":{\"general\":\"--dpi-desync=fake\"},\"roles\":{}}")]   // повтор ключа
    public void NotAnObject_OrDuplicateKey_IsBroken(string json) => Assert.Equal("Строка повреждена", Reason(() => StrategyExchange.Parse(Raw(json), Exists)));

    [Fact]
    public void ExtraFieldsMissingFieldsAndWrongTypes_Rejected()
    {
        Assert.Contains("Лишнее поле", Reason(() => StrategyExchange.Parse(Raw("{\"title\":\"a\",\"roles\":{\"general\":\"--dpi-desync=fake\"},\"cmd\":\"calc\"}"), Exists)));
        Assert.Contains("нет названия", Reason(() => StrategyExchange.Parse(Raw("{\"roles\":{\"general\":\"--dpi-desync=fake\"}}"), Exists)));
        Assert.Contains("нет ролей", Reason(() => StrategyExchange.Parse(Raw("{\"title\":\"a\"}"), Exists)));
        Assert.Contains("нужна строка", Reason(() => StrategyExchange.Parse(Raw("{\"title\":\"a\",\"roles\":{\"general\":5}}"), Exists)));
        Assert.Contains("нет ни одной роли", Reason(() => StrategyExchange.Parse(Raw("{\"title\":\"a\",\"roles\":{}}"), Exists)));
    }

    // ---------- белый список: попытки обойти ----------

    [Theory]
    [InlineData("--wf-raw=true", "Параметр --wf-raw не разрешён")]
    [InlineData("--dpi-desync=fake --wf-tcp=1-65535", "Параметр --wf-tcp не разрешён")]
    [InlineData("--dpi-desync=fake --new", "Параметр --new не разрешён")]
    [InlineData("--dpi-desync=fake --hostlist=C:/x.txt", "Параметр --hostlist не разрешён")]
    [InlineData("--dpi-desync=fake --debug", "Параметр --debug не разрешён")]
    [InlineData("--dpi-desync=fake --pidfile=x", "Параметр --pidfile не разрешён")]
    [InlineData("--dpi-desync=fake --dpi-desync-any-protocol=1", "только в игровых ролях")]
    public void NotAllowedKeys_NamedInReason(string args, string reason) =>
        Assert.Contains(reason, Reason(() => StrategyExchange.Parse(One("general", args), Exists)));

    [Theory]
    [InlineData(@"--dpi-desync-fake-tls=C:\Windows\System32\cmd.exe")]
    [InlineData("--dpi-desync-fake-tls=C:/Windows/x.bin")]
    [InlineData("--dpi-desync-fake-tls=/etc/passwd")]
    [InlineData("--dpi-desync-fake-tls=../core/winws.exe")]
    [InlineData("--dpi-desync-fake-tls={fake}/../core/winws.exe")]
    [InlineData("--dpi-desync-fake-tls={fake}/sub/x.bin")]
    [InlineData("--dpi-desync-fake-tls=cygwin1.dll")]               // голое имя: winws искал бы его в core\
    [InlineData("--dpi-desync-fake-tls=\\\\server\\share\\x.bin")]
    [InlineData("--dpi-desync-split-seqovl-pattern=C:/x.bin")]
    [InlineData("--dpi-desync-fake-quic=%APPDATA%/x.bin")]
    public void ForeignPaths_Rejected(string args) => Assert.NotEmpty(Reason(() => StrategyExchange.Parse(One("general", args), Exists)));

    [Theory]
    [InlineData("--dpi-desync=fake\"&calc")]
    [InlineData("--dpi-desync=\"fake\"")]
    [InlineData("--dpi-desync=fake & calc")]
    [InlineData("--dpi-desync=fake && calc")]
    [InlineData("--dpi-desync=fake | more")]
    [InlineData("--dpi-desync=fake;calc")]
    [InlineData("--dpi-desync=fake`calc`")]
    [InlineData("--dpi-desync=$(calc)")]
    [InlineData("--dpi-desync=fake\n--wf-raw=x")]
    [InlineData("--dpi-desync=fake\t--wf-raw=x")]
    [InlineData("--dpi-desync=fake\r")]
    [InlineData("--dpi-desync=fake\n")]
    [InlineData("--dpi-desync=fake\0")]
    [InlineData("--dpi-desync=fake^&calc")]
    [InlineData("--dpi-desync=%PATH%")]
    [InlineData("--dpi-desync=fake>out.txt")]
    [InlineData("calc.exe")]
    [InlineData("-x")]
    public void QuotesAmpersandsAndControlChars_Rejected(string args)
    {
        var r = Reason(() => StrategyExchange.Parse(One("general", args), Exists));
        Assert.True(r.Length is > 0 and < 160, r);
    }

    [Fact]
    public void UnknownRole_AndEmptyRole_AndHugeRole_Rejected()
    {
        Assert.Contains("Неизвестная роль", Reason(() => StrategyExchange.Parse(One("everything", "--dpi-desync=fake"), Exists)));
        Assert.Contains("Неизвестная роль", Reason(() => StrategyExchange.Parse(One("../../x", "--dpi-desync=fake"), Exists)));
        Assert.Contains("пуста", Reason(() => StrategyExchange.Parse(One("general", "   "), Exists)));
        Assert.Contains("слишком длинная", Reason(() => StrategyExchange.Parse(One("general", string.Join(' ', Enumerable.Repeat("--dpi-desync=fake", 200))), Exists)));
    }

    [Fact]
    public void FakeFile_MustExistInProgramFolder()
    {
        var args = "--dpi-desync=fake --dpi-desync-fake-tls={fake}/nope.bin";
        Assert.Contains("Файла nope.bin нет в папке программы", Reason(() => StrategyExchange.Parse(One("general", args), Exists)));
        // Есть в папке — принимается.
        var ok = StrategyExchange.Parse(One("general", "--dpi-desync=fake --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin"), Exists);
        Assert.Single(ok.Roles);
        // Игровые параметры — в игровых ролях.
        Assert.Single(StrategyExchange.Parse(One("game_udp", "--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n2"), Exists).Roles);
    }

    [Fact]
    public void Title_StripsControlAndDirectionalMarks_AndTruncates()
    {
        Assert.Equal("a b c", StrategyExchange.CleanTitle("a\r\nb\tc"));
        Assert.Equal("ab", StrategyExchange.CleanTitle("a\u202Eb"));   // RLO: подмена порядка знаков в названии
        Assert.Equal("Без названия", StrategyExchange.CleanTitle(" \u0000 "));
        Assert.Equal(StrategyExchange.MaxTitle, StrategyExchange.CleanTitle(new string('я', 500)).Length);
        var s = StrategyExchange.Parse(Raw("{\"title\":\"Моя\\u202e\\nстратегия\",\"roles\":{\"general\":\"--dpi-desync=fake\"}}"), Exists);
        Assert.Equal("Моя стратегия", s.Title);
    }

    // ---------- строка запуска из .bat Flowseal ----------

    /// <summary>Обратная сборка: роли каталога → строка как в general*.bat (фильтры, списки, %BIN%, ^!, продолжения строк).</summary>
    static string ToBat(Strategy s)
    {
        string Filter(string role) => role switch
        {
            "quic" => "--filter-udp=443 --hostlist=\"%LISTS%list-general.txt\" --hostlist-exclude=\"%LISTS%list-exclude.txt\" --ipset-exclude=\"%LISTS%ipset-exclude.txt\"",
            "discord_udp" => "--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun",
            "discord_media" => "--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media",
            "google" => "--filter-tcp=443 --hostlist=\"%LISTS%list-google.txt\"",
            "general" => "--filter-tcp=80,443 --hostlist=\"%LISTS%list-general.txt\" --hostlist-exclude=\"%LISTS%list-exclude.txt\" --ipset-exclude=\"%LISTS%ipset-exclude.txt\"",
            "ipset_udp" => "--filter-udp=443 --ipset=\"%LISTS%ipset-all.txt\" --hostlist-exclude=\"%LISTS%list-exclude.txt\"",
            "ipset_tcp" => "--filter-tcp=80,443,8443 --ipset=\"%LISTS%ipset-all.txt\" --hostlist-exclude=\"%LISTS%list-exclude.txt\"",
            "game_tcp" => "--filter-tcp=%GameFilterTCP% --ipset=\"%LISTS%ipset-all.txt\" --ipset-exclude=\"%LISTS%ipset-exclude.txt\"",
            "game_udp" => "--filter-udp=%GameFilterUDP% --ipset=\"%LISTS%ipset-all.txt\" --ipset-exclude=\"%LISTS%ipset-exclude.txt\"",
            _ => throw new InvalidOperationException(role),
        };
        string Desync(string args) => string.Join(' ', args.Split(' ').Select(t =>
        {
            var eq = t.IndexOf('=');
            if (eq < 0) return t;
            var v = t[(eq + 1)..];
            v = v.StartsWith("{fake}/") ? "\"%BIN%" + v["{fake}/".Length..] + "\"" : v == "!" ? "^!" : v;
            return t[..(eq + 1)] + v;
        }));
        var profiles = WinwsArgs.RoleOrder.Where(s.Roles.ContainsKey).Select(r => Filter(r) + " " + Desync(s.Roles[r])).ToList();
        var sb = new StringBuilder();
        sb.Append("@echo off\r\nchcp 65001 > nul\r\ncd /d \"%~dp0\"\r\nset \"BIN=%~dp0bin\\\"\r\nset \"LISTS=%~dp0lists\\\"\r\ncd /d %BIN%\r\n\r\n");
        sb.Append("start \"zapret: %~n0\" /min \"%BIN%winws.exe\" --wf-tcp=80,443,2053,2083,2087,2096,8443,%GameFilterTCP% --wf-udp=443,19294-19344,50000-50100,%GameFilterUDP% ^\r\n");
        for (int i = 0; i < profiles.Count; i++) sb.Append(profiles[i]).Append(i < profiles.Count - 1 ? " --new ^\r\n" : "\r\n");
        return sb.ToString();
    }

    [Fact]
    public void Bat_EveryCatalogStrategy_BuiltBackAndParsedToSameRoles()
    {
        var catalog = StrategyCatalog.Load(Repo.Strategies);
        Assert.Equal(20, catalog.Strategies.Count);
        foreach (var s in catalog.Strategies)
        {
            var bat = ToBat(s);
            var parsed = StrategyExchange.Parse(bat, Exists);
            Assert.True(parsed.FromBat);
            Assert.Equal(s.Roles.OrderBy(r => r.Key), parsed.Roles.OrderBy(r => r.Key));
        }
    }

    [Fact]
    public void Bat_ExclamationCaret_BecomesBuiltInFake()
    {
        var bat = "start \"z\" /min \"%BIN%winws.exe\" --filter-tcp=80,443 --hostlist=\"%LISTS%list-general.txt\" --dpi-desync=fake --dpi-desync-fake-tls=^! --dpi-desync-fake-tls=\"%BIN%tls_clienthello_www_google_com.bin\"";
        var r = StrategyExchange.Parse(bat, Exists);
        Assert.Equal("--dpi-desync=fake --dpi-desync-fake-tls=! --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin", r.Roles["general"]);
    }

    [Fact]
    public void Bat_ArgumentsOnlyWithoutWinwsExe_Accepted()
    {
        var r = StrategyExchange.Parse("--wf-tcp=80,443 --filter-tcp=443 --hostlist=lists/list-google.txt --dpi-desync=multisplit --dpi-desync-split-pos=1", Exists);
        Assert.Equal("--dpi-desync=multisplit --dpi-desync-split-pos=1", r.Roles["google"]);
    }

    [Fact]
    public void Bat_DiscordHostlist_IsDiscordTcpRole_NotGeneral()
    {
        var r = StrategyExchange.Parse("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%list-discord.txt\" --dpi-desync=multisplit --dpi-desync-split-pos=1 "
            + "--new --filter-tcp=80,443 --hostlist=\"%LISTS%list-general.txt\" --dpi-desync=fake", Exists);
        Assert.Equal("--dpi-desync=multisplit --dpi-desync-split-pos=1", r.Roles["discord_tcp"]);
        Assert.Equal("--dpi-desync=fake", r.Roles["general"]);
        Assert.Null(StrategyExchange.CheckRoles(r.Roles));
    }

    [Theory]
    // два профиля на одну роль
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake --new --filter-tcp=80,443 --hostlist=\"%LISTS%b.txt\" --dpi-desync=split", "Два профиля")]
    // порты не те
    [InlineData("winws.exe --filter-tcp=5222 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake", "Не удалось отнести профиль")]
    // нет фильтра
    [InlineData("winws.exe --dpi-desync=fake", "нет --filter-tcp")]
    // TCP и UDP сразу
    [InlineData("winws.exe --filter-tcp=443 --filter-udp=443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake", "TCP и UDP")]
    // неразрешённые ключи
    [InlineData("winws.exe --wf-raw=x --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake", "Параметр --wf-raw не разрешён")]
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake --debug", "Параметр --debug не разрешён")]
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist-auto=x.txt --dpi-desync=fake", "--hostlist-auto не разрешён")]
    // чужой файл списка
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"C:\\Windows\\win.ini\" --dpi-desync=fake", "Путь вне папки программы")]
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%..\\..\\x.txt\" --dpi-desync=fake", "Путь вне папки программы")]
    // чужой путь в значении
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake --dpi-desync-fake-tls=\"C:\\x\\evil.bin\"", "недопустимые символы")]
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake --dpi-desync-fake-tls=\"%BIN%..\\core\\winws.exe\"", "неизвестная переменная")]
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake --dpi-desync-fake-tls=%SystemRoot%\\x.bin", "неизвестная переменная")]
    // несуществующий fake
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake --dpi-desync-fake-tls=\"%BIN%nope.bin\"", "Файла nope.bin нет")]
    // кавычки, &, ^
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake & calc", "не параметр winws")]
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake&calc", "недопустимые символы")]
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=\"fake --wf-raw=x\"", "недопустимые символы")]
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake \"", "кавычка")]
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake ^& calc", "управляющие символы")]
    [InlineData("winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake | more", "не параметр winws")]
    // не наша строка
    [InlineData("hello world", "Это не строка DpiShell")]
    public void Bat_Refusals_GiveReason(string bat, string reason) => Assert.Contains(reason, Reason(() => StrategyExchange.Parse(bat, Exists)), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Bat_TwoWinwsLines_Refused() =>
        Assert.Contains("несколько запусков", Reason(() => StrategyExchange.Parse(
            "start winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake\r\nstart winws.exe --filter-tcp=443 --hostlist=\"%LISTS%b.txt\" --dpi-desync=fake", Exists)));

    [Fact]
    public void Bat_Huge_Refused() =>
        Assert.Contains("слишком длинный", Reason(() => StrategyExchange.Parse("winws.exe " + new string('x', 20_000), Exists)));

    [Fact]
    public void Bat_GameRoles_OnlyWithGameFilters()
    {
        var r = StrategyExchange.Parse("winws.exe --filter-udp=%GameFilterUDP% --ipset=\"%LISTS%ipset-all.txt\" --dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n2", Exists);
        Assert.Equal(["game_udp"], r.Roles.Keys);
        // Игровой параметр в обычном профиле — отказ.
        Assert.Contains("игровых ролях", Reason(() => StrategyExchange.Parse(
            "winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake --dpi-desync-any-protocol=1", Exists)));
    }

    // ---------- хранение своих ----------

    [Fact]
    public void UserStrategies_AddPersonal_SavesUnderNewId_AndRemoves()
    {
        using var tmp = new TempDir();
        var p = new AppPaths(tmp.Sub("root"), Repo.Data);
        Directory.CreateDirectory(p.Fake);
        File.WriteAllBytes(Path.Combine(p.Fake, "tls_clienthello_www_google_com.bin"), [1]);

        var roles = new Dictionary<string, string> { ["general"] = "--dpi-desync=fake --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin" };
        var a = UserStrategies.AddPersonal(p, "Моя  \n первая", roles, "user");
        var b = UserStrategies.AddPersonal(p, "Моя первая", roles, "pasted");
        Assert.NotEqual(a.Id, b.Id);
        Assert.StartsWith("own-", a.Id);
        Assert.Equal("Моя первая", a.Title);
        Assert.Equal(["user", "pasted"], UserStrategies.Load(p).Select(s => s.Source));

        Assert.True(UserStrategies.Remove(p, a.Id));
        Assert.False(UserStrategies.Remove(p, a.Id));
        Assert.Equal([b.Id], UserStrategies.Load(p).Select(s => s.Id));
    }

    [Fact]
    public void UserStrategies_AddPersonal_ChecksAgain_BeforeWritingFile()
    {
        using var tmp = new TempDir();
        var p = new AppPaths(tmp.Sub("root"), Repo.Data);
        Directory.CreateDirectory(p.Fake);
        Assert.Throws<InvalidDataException>(() => UserStrategies.AddPersonal(p, "x", new Dictionary<string, string> { ["general"] = "--wf-raw=1" }, "user"));
        Assert.Throws<InvalidDataException>(() => UserStrategies.AddPersonal(p, "x", new Dictionary<string, string> { ["general"] = "--dpi-desync-fake-tls={fake}/missing.bin" }, "user"));
        Assert.Throws<InvalidDataException>(() => UserStrategies.AddPersonal(p, "x", new Dictionary<string, string>(), "user"));
        Assert.Throws<ArgumentException>(() => UserStrategies.AddPersonal(p, "x", new Dictionary<string, string> { ["general"] = "--dpi-desync=fake" }, "search"));
        Assert.False(File.Exists(UserStrategies.File(p)));
    }

    [Fact]
    public void UserStrategies_AddPersonal_LimitsCount()
    {
        using var tmp = new TempDir();
        var p = new AppPaths(tmp.Sub("root"), Repo.Data);
        var roles = new Dictionary<string, string> { ["general"] = "--dpi-desync=fake" };
        for (int i = 0; i < UserStrategies.MaxPersonal; i++) UserStrategies.AddPersonal(p, "s" + i, roles, "user");
        Assert.Contains("уже 50", Assert.Throws<InvalidOperationException>(() => UserStrategies.AddPersonal(p, "лишняя", roles, "user")).Message);
    }

    // ---------- команды помощника ----------

    static HelperCall Parse(string json) => HelperCommands.Parse(json, new FakeHelperBackend());

    [Fact]
    public void Helper_StrategyAdd_AcceptsValid_AndTypesArguments()
    {
        var c = Parse("{\"id\":1,\"cmd\":\"strategy_add\",\"args\":{\"title\":\"Моя\",\"roles\":{\"general\":\"--dpi-desync=fake --dpi-desync-repeats=6\"},\"source\":\"pasted\"}}");
        Assert.Equal("Моя", c.Str("title"));
        Assert.Equal("pasted", c.Str("source"));
        Assert.Equal("--dpi-desync=fake --dpi-desync-repeats=6", c.Map("roles")!["general"]);
    }

    [Theory]
    [InlineData("{\"general\":\"--wf-raw=1\"}", "не разрешён")]
    [InlineData("{\"general\":\"--dpi-desync=fake --new --filter-tcp=1\"}", "не разрешён")]
    [InlineData("{\"general\":\"--dpi-desync-fake-tls=C:/x.bin\"}", "недопустимые символы")]
    [InlineData("{\"general\":\"--dpi-desync-fake-tls=/etc/passwd\"}", "Путь вне папки")]
    [InlineData("{\"general\":\"--dpi-desync=fake\\\"\"}", "недопустимые символы")]
    [InlineData("{\"general\":\"--dpi-desync=fake &calc\"}", "не параметр winws")]
    [InlineData("{\"bogus\":\"--dpi-desync=fake\"}", "Неизвестная роль")]
    [InlineData("{}", "объект")]
    [InlineData("[]", "объект")]
    [InlineData("{\"general\":5}", "строка")]
    [InlineData("{\"general\":\"\"}", "строка")]
    public void Helper_StrategyAdd_RejectsHostileRoles(string roles, string reason)
    {
        var ex = Assert.Throws<HelperRequestException>(() => Parse("{\"id\":1,\"cmd\":\"strategy_add\",\"args\":{\"title\":\"x\",\"roles\":" + roles + ",\"source\":\"user\"}}"));
        Assert.Contains(reason, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Helper_StrategyAdd_RejectsBadTitleSourceAndExtraFields()
    {
        string Add(string args) => "{\"id\":1,\"cmd\":\"strategy_add\",\"args\":" + args + "}";
        const string roles = "\"roles\":{\"general\":\"--dpi-desync=fake\"}";
        Assert.Throws<HelperRequestException>(() => Parse(Add("{\"title\":\"a\\nb\"," + roles + ",\"source\":\"user\"}")));          // перевод строки
        Assert.Throws<HelperRequestException>(() => Parse(Add("{\"title\":\"" + new string('я', 61) + "\"," + roles + ",\"source\":\"user\"}")));
        Assert.Throws<HelperRequestException>(() => Parse(Add("{\"title\":\"a\"," + roles + ",\"source\":\"search\"}")));            // найденные ставит только поиск
        Assert.Throws<HelperRequestException>(() => Parse(Add("{\"title\":\"a\"," + roles + ",\"source\":\"user\",\"id\":\"alt9\"}")));   // id назначает помощник
        Assert.Throws<HelperRequestException>(() => Parse(Add("{\"title\":\"a\"," + roles + "}")));                                   // нет source
        Assert.Throws<HelperRequestException>(() => Parse(Add("{\"title\":\"a\",\"roles\":{\"general\":\"--dpi-desync=fake\",\"general\":\"x\"},\"source\":\"user\"}")));   // повтор ключа
    }

    [Fact]
    public void Helper_StrategyDeleteAndCheck_TakeOnlyKnownIds()
    {
        Assert.Equal("alt9", Parse("{\"id\":1,\"cmd\":\"strategy_delete\",\"args\":{\"strategy\":\"alt9\"}}").Str("strategy"));
        Assert.Throws<HelperRequestException>(() => Parse("{\"id\":1,\"cmd\":\"strategy_delete\",\"args\":{\"strategy\":\"..\\\\x\"}}"));
        Assert.Throws<HelperRequestException>(() => Parse("{\"id\":1,\"cmd\":\"strategy_check\",\"args\":{\"strategy\":\"нет-такой\"}}"));
        var c = Parse("{\"id\":1,\"cmd\":\"strategy_check\",\"args\":{\"strategy\":\"alt9\",\"groups\":[\"youtube\"]}}");
        Assert.True(c.Spec.Long);
        Assert.Equal(["youtube"], c.List("groups"));
    }

    [Fact]
    public async Task Helper_RealBackend_AddsAndDeletes_ButNeverCatalogOrSelected()
    {
        using var tmp = new TempDir();
        var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
        shell.Paths.EnsureDirectories();
        File.Copy(Path.Combine(FakeDir, "tls_clienthello_www_google_com.bin"), Path.Combine(shell.Paths.Fake, "tls_clienthello_www_google_com.bin"));
        var b = PcHelper.Over(shell);

        var s = b.AddStrategy("Вставленная", new Dictionary<string, string> { ["general"] = "--dpi-desync=fake --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin" }, "pasted");
        Assert.Contains(s.Id, b.StrategyIds);
        Assert.Equal("pasted", b.Status().Strategies.First(x => x.Id == s.Id).Source);
        Assert.Throws<InvalidDataException>(() => b.AddStrategy("Чужой", new Dictionary<string, string> { ["general"] = "--dpi-desync-fake-tls={fake}/x.bin" }, "pasted"));

        Assert.Throws<InvalidOperationException>(() => b.DeleteStrategy("alt9"));    // готовая
        shell.State.StrategyId = s.Id;
        Assert.Contains("выбрана", Assert.Throws<InvalidOperationException>(() => b.DeleteStrategy(s.Id)).Message);
        shell.State.StrategyId = "alt9";
        b.DeleteStrategy(s.Id);
        Assert.DoesNotContain(s.Id, b.StrategyIds);
        await Task.CompletedTask;
    }
}
