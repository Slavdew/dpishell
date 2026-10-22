using System.Security.Cryptography;
using System.Text;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// SSH к роутеру в памяти: записывает команды и stdin, держит «файлы» роутера (загрузки, /tmp/dpishell-uninstall.json),
/// отвечает на sha256sum/cat/rm/mkdir; вывод сценариев (sh -s) задаёт тест через <see cref="OnScript"/>.
/// </summary>
public sealed class FakeSsh : ISshSession
{
    public RouterSshTarget Target { get; } = new("192.168.1.1");
    public List<(string Command, string? Stdin)> Runs { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<string> Uploads { get; } = [];
    public List<(int Remote, int Local)> Forwards { get; } = [];
    public int ForwardsClosed { get; private set; }
    /// <summary>(команда, текст сценария) → stdout. Строки отдаются в onLine по одной.</summary>
    public Func<string, string, string> OnScript { get; set; } = (_, _) => "";
    /// <summary>Подменить содержимое при загрузке (проверка «хеш на роутере не совпал»).</summary>
    public Func<byte[], byte[]>? CorruptUpload { get; set; }
    public bool Disposed { get; private set; }

    public async Task<SshRunResult> RunAsync(string command, Stream? stdin = null, Action<string>? onLine = null, CancellationToken ct = default)
    {
        string? input = null;
        if (stdin is not null) { using var r = new StreamReader(stdin, Encoding.UTF8, leaveOpen: true); input = await r.ReadToEndAsync(ct); }
        lock (Runs) Runs.Add((command, input));
        string stdout = "";
        int code = 0;
        if (command == RouterSsh.WebCertCommand)
        {
            if (WebCertOutput is { } w) stdout = w; else code = 3;
        }
        else if (command.Contains("sh -s"))
            stdout = OnScript(command, input ?? "");
        else if (command.StartsWith("sha256sum "))
        {
            var path = Unquote(command["sha256sum ".Length..].Replace(" 2>/dev/null", ""));
            if (Files.TryGetValue(path, out var b)) stdout = Convert.ToHexStringLower(SHA256.HashData(b)) + "  " + path + "\n";
            else code = 1;
        }
        else if (command.StartsWith("cat ") && command.EndsWith("2>/dev/null"))
        {
            var path = command.Split(' ')[1];
            stdout = string.Join("", Files.Where(f => f.Key == path).Select(f => Encoding.UTF8.GetString(f.Value)));
            if (path.StartsWith("/etc/")) stdout = FeedFilesText;
        }
        else if (command.StartsWith("rm -f ")) Files.Remove(Unquote(command["rm -f ".Length..]));
        else if (command.StartsWith("rm -rf /tmp/dpishell-update"))
            foreach (var k in Files.Keys.Where(k => k.StartsWith("/tmp/dpishell-update/")).ToList()) Files.Remove(k);
        foreach (var line in stdout.Split('\n')) if (line.Length > 0) onLine?.Invoke(line);
        return new SshRunResult(code, stdout, "");
    }

    public string FeedFilesText { get; set; } = "";
    /// <summary>Ответ на чтение сертификата панели (<see cref="RouterSsh.WebCertCommand"/>); null — файла нет.</summary>
    public string? WebCertOutput { get; set; }

    static string Unquote(string s) => s.Trim().Trim('\'');

    public async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        Files[remotePath] = CorruptUpload?.Invoke(bytes) ?? bytes;
        Uploads.Add(remotePath);
    }

    public Task<IAsyncDisposable> ForwardRemoteAsync(int remotePort, int localPort, CancellationToken ct = default)
    {
        Forwards.Add((remotePort, localPort));
        return Task.FromResult<IAsyncDisposable>(new Closer(() => ForwardsClosed++));
    }

    sealed class Closer(Action a) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { a(); return ValueTask.CompletedTask; }
    }

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

public sealed class FakeSshConnector(FakeSsh ssh) : ISshConnector
{
    public string? LastPassword { get; private set; }
    public Task<ISshSession> ConnectAsync(RouterSshTarget target, Func<string> password, CancellationToken ct = default)
    {
        LastPassword = password();
        return Task.FromResult<ISshSession>(ssh);
    }
}
